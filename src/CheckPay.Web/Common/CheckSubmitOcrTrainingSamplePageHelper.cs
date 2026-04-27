using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Web.Common;

/// <summary>上传页 / 复核页在支票最终入库成功后追加 OCR 训练样本。</summary>
public static class CheckSubmitOcrTrainingSamplePageHelper
{
    private const string VerbosityOff = "Off";
    private const string VerbosityVerbose = "Verbose";

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
            var exists = await db.OcrTrainingSamples.AsNoTracking()
                .AnyAsync(
                    s => s.DocumentType == "check"
                         && s.Notes != null
                         && s.Notes.Contains(marker, StringComparison.Ordinal),
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

        Guid? templateId = null;
        if (templateResolver != null)
        {
            var dto = SubmitCheckOcrTrainingSampleFactory.TryDeserializePrimaryCheckDto(doc);
            if (dto != null)
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
}
