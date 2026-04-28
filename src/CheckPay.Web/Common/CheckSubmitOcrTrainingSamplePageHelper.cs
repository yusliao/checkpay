using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CheckPay.Web.Common;

/// <summary>上传页 / 复核页在支票最终入库成功后追加 OCR 训练样本。</summary>
public static class CheckSubmitOcrTrainingSamplePageHelper
{
    private const string VerbosityOff = "Off";
    private const string VerbosityVerbose = "Verbose";
    private const double AutoSampleMaxChangedFieldConfidence = 0.85;

    public static async Task TryAppendAfterCheckFinalSubmitAsync(
        IApplicationDbContext db,
        IConfiguration configuration,
        string imageUrl,
        Guid? ocrResultId,
        string submittedCheckNumber,
        decimal submittedAmount,
        DateTime submittedCheckDateUtc,
        CheckAchExtensionData submittedAch,
        Guid checkRecordId,
        ICheckOcrTemplateResolver? templateResolver = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var verbosity = configuration["Ocr:Training:AutoSampleLogVerbosity"]?.Trim() ?? "Minimal";
        if (string.Equals(verbosity, VerbosityOff, StringComparison.OrdinalIgnoreCase))
            logger = null;

        var verbose = string.Equals(verbosity, VerbosityVerbose, StringComparison.OrdinalIgnoreCase);

        if (!configuration.GetValue("Ocr:Training:AutoSampleOnCheckSubmit", true))
        {
            if (verbose)
                logger?.LogDebug("OcrTrainingAutoSample skipped: AutoSampleOnCheckSubmit=false");
            return;
        }

        if (ocrResultId is null || ocrResultId == Guid.Empty)
        {
            if (verbose)
                logger?.LogDebug("OcrTrainingAutoSample skipped: missing OcrResultId");
            return;
        }

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            if (verbose)
                logger?.LogDebug("OcrTrainingAutoSample skipped: empty ImageUrl");
            return;
        }

        var ocrEntity = await db.OcrResults.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == ocrResultId.Value, cancellationToken);
        var doc = ocrEntity?.RawResult ?? ocrEntity?.AzureRawResult;
        if (doc == null)
        {
            if (verbose)
                logger?.LogDebug(
                    "OcrTrainingAutoSample skipped: no RawResult/AzureRawResult for OcrResultId={OcrResultId}",
                    ocrResultId);
            return;
        }

        if (configuration.GetValue("Ocr:Training:AutoSampleDedupByOcrResultId", true))
        {
            var marker = $"ocrResultId={ocrResultId.Value:D}";
            var markerLike = $"%{marker}%";
            var exists = await db.OcrTrainingSamples.AsNoTracking()
                .AnyAsync(
                    s => s.DocumentType == "check"
                         && s.Notes != null
                         && EF.Functions.Like(s.Notes, markerLike),
                    cancellationToken);
            if (exists)
            {
                logger?.LogInformation(
                    "OcrTrainingAutoSample skipped: dedup by OcrResultId={OcrResultId} CheckRecordId={CheckRecordId}",
                    ocrResultId,
                    checkRecordId);
                return;
            }
        }

        var dto = SubmitCheckOcrTrainingSampleFactory.TryDeserializePrimaryCheckDto(doc);
        if (dto == null)
        {
            if (verbose)
            {
                logger?.LogDebug(
                    "OcrTrainingAutoSample skipped: cannot deserialize OCR payload OcrResultId={OcrResultId} CheckRecordId={CheckRecordId}",
                    ocrResultId,
                    checkRecordId);
            }
            return;
        }

        var confidenceDoc = ocrEntity?.RawResult != null
            ? ocrEntity?.ConfidenceScores
            : ocrEntity?.AzureConfidenceScores;

        if (!HasLowConfidenceCorrection(
                dto,
                submittedCheckNumber,
                submittedAmount,
                submittedCheckDateUtc,
                submittedAch,
                confidenceDoc,
                AutoSampleMaxChangedFieldConfidence))
        {
            if (verbose)
            {
                logger?.LogDebug(
                    "OcrTrainingAutoSample skipped: no low-confidence corrected fields OcrResultId={OcrResultId} CheckRecordId={CheckRecordId}",
                    ocrResultId,
                    checkRecordId);
            }
            return;
        }

        Guid? templateId = null;
        if (templateResolver != null)
        {
            var resolution = await templateResolver.ResolveAsync(
                submittedAch.RoutingNumber,
                dto.ExtractedText ?? string.Empty,
                cancellationToken);
            templateId = resolution.TemplateId;
            if (verbose && templateId.HasValue)
            {
                logger?.LogDebug(
                    "OcrTrainingAutoSample template resolved TemplateId={TemplateId}",
                    templateId);
            }
        }

        var requireDiff = configuration.GetValue("Ocr:Training:AutoSampleRequireDiff", true);
        var sample = SubmitCheckOcrTrainingSampleFactory.TryCreateFromCheckFinalSubmit(
            doc,
            imageUrl,
            ocrResultId.Value,
            submittedCheckNumber,
            submittedAmount,
            submittedCheckDateUtc,
            submittedAch,
            requireDiff,
            checkRecordId,
            templateId);
        if (sample == null)
        {
            if (verbose)
            {
                logger?.LogDebug(
                    "OcrTrainingAutoSample skipped: factory returned null (e.g. no structured diff) OcrResultId={OcrResultId} CheckRecordId={CheckRecordId} RequireDiff={RequireDiff}",
                    ocrResultId,
                    checkRecordId,
                    requireDiff);
            }

            return;
        }

        try
        {
            db.OcrTrainingSamples.Add(sample);
            await db.SaveChangesAsync(cancellationToken);
            logger?.LogInformation(
                "OcrTrainingAutoSample appended OcrResultId={OcrResultId} CheckRecordId={CheckRecordId} TemplateId={TemplateId}",
                ocrResultId,
                checkRecordId,
                templateId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "OcrTrainingAutoSample save failed after check commit OcrResultId={OcrResultId} CheckRecordId={CheckRecordId}",
                ocrResultId,
                checkRecordId);
        }
    }

    private static bool HasLowConfidenceCorrection(
        OcrResultDto ocr,
        string submittedCheckNumber,
        decimal submittedAmount,
        DateTime submittedDateUtc,
        CheckAchExtensionData submittedAch,
        JsonDocument? confidenceDoc,
        double maxConfidence)
    {
        var map = BuildConfidenceMap(confidenceDoc);
        var ocrAch = CheckAchExtensionData.FromOcrResult(ocr);

        if (IsChanged(ocr.CheckNumber, submittedCheckNumber)
            && GetConfidence(map, "CheckNumber") <= maxConfidence)
            return true;
        if (ocr.Amount != submittedAmount && GetConfidence(map, "Amount") <= maxConfidence)
            return true;
        if (ocr.Date.Date != submittedDateUtc.Date && GetConfidence(map, "Date") <= maxConfidence)
            return true;

        if (IsChanged(ocrAch.RoutingNumber, submittedAch.RoutingNumber)
            && GetConfidence(map, "RoutingNumber") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.AccountNumber, submittedAch.AccountNumber)
            && GetConfidence(map, "AccountNumber") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.BankName, submittedAch.BankName)
            && GetConfidence(map, "BankName") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.AccountHolderName, submittedAch.AccountHolderName)
            && GetConfidence(map, "AccountHolderName") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.PayToOrderOf, submittedAch.PayToOrderOf)
            && GetConfidence(map, "PayToOrderOf") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.ForMemo, submittedAch.ForMemo)
            && GetConfidence(map, "ForMemo") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.MicrLineRaw, submittedAch.MicrLineRaw)
            && GetConfidence(map, "MicrLineRaw") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.CheckNumberMicr, submittedAch.CheckNumberMicr)
            && GetConfidence(map, "CheckNumberMicr") <= maxConfidence)
            return true;
        if (IsChanged(ocrAch.CompanyName, submittedAch.CompanyName)
            && GetConfidence(map, "CompanyName") <= maxConfidence)
            return true;

        return false;
    }

    private static Dictionary<string, double> BuildConfidenceMap(JsonDocument? confidenceDoc)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (confidenceDoc?.RootElement.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var prop in confidenceDoc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number)
                map[prop.Name] = prop.Value.GetDouble();
            else if (prop.Value.ValueKind == JsonValueKind.String
                     && double.TryParse(prop.Value.GetString(), out var parsed))
                map[prop.Name] = parsed;
        }

        return map;
    }

    private static double GetConfidence(Dictionary<string, double> map, string field)
        => map.TryGetValue(field, out var value) ? value : 0.5;

    private static bool IsChanged(string? ocrValue, string? submittedValue)
        => !string.Equals(Norm(ocrValue), Norm(submittedValue), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
