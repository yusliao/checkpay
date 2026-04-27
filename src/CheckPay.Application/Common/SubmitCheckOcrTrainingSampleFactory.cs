using System.Text.Json;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;

namespace CheckPay.Application.Common;

/// <summary>支票「最终提交入库」时由 OCR 快照与表单值生成训练样本（与标注页字段对齐）。</summary>
public static class SubmitCheckOcrTrainingSampleFactory
{
    public const string AutoSubmitNotesPrefix = "auto:check-final-submit";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>供票型解析等场景复用，与 <see cref="TryCreateFromCheckFinalSubmit"/> 使用相同反序列化选项。</summary>
    public static OcrResultDto? TryDeserializePrimaryCheckDto(JsonDocument rawPayload)
    {
        try
        {
            return JsonSerializer.Deserialize<OcrResultDto>(rawPayload.RootElement.GetRawText(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string BuildAutoSubmitNotes(Guid ocrResultId, Guid? checkRecordId)
    {
        var parts = new List<string> { AutoSubmitNotesPrefix, $"ocrResultId={ocrResultId:D}" };
        if (checkRecordId.HasValue)
            parts.Add($"checkRecordId={checkRecordId.Value:D}");
        return string.Join(';', parts);
    }

    /// <param name="requireStructuredDiff">为 true 时，OCR 与提交值完全一致则不生成样本。</param>
    public static OcrTrainingSample? TryCreateFromCheckFinalSubmit(
        JsonDocument rawPayload,
        string imageUrl,
        Guid ocrResultId,
        string submittedCheckNumber,
        decimal submittedAmount,
        DateTime submittedDateUtc,
        CheckAchExtensionData submittedAch,
        bool requireStructuredDiff = true,
        Guid? checkRecordId = null,
        Guid? ocrCheckTemplateId = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        var dto = TryDeserializePrimaryCheckDto(rawPayload);
        if (dto == null)
            return null;

        var ocrAch = CheckAchExtensionData.FromOcrResult(dto);
        var cn = submittedCheckNumber.Trim();
        if (requireStructuredDiff
            && !HasStructuredDiff(dto, cn, submittedAmount, submittedDateUtc, ocrAch, submittedAch))
            return null;

        var panelText = OcrCheckCopyPanelFormatter.FromJsonDocument(rawPayload);

        return new OcrTrainingSample
        {
            ImageUrl = imageUrl.Trim(),
            DocumentType = "check",
            OcrRawResponse = panelText,
            OcrCheckNumber = string.IsNullOrWhiteSpace(dto.CheckNumber) ? null : dto.CheckNumber.Trim(),
            OcrAmount = dto.Amount,
            OcrDate = DateTime.SpecifyKind(dto.Date, DateTimeKind.Utc),
            CorrectCheckNumber = cn,
            CorrectAmount = submittedAmount,
            CorrectDate = DateTime.SpecifyKind(submittedDateUtc, DateTimeKind.Utc),
            Notes = BuildAutoSubmitNotes(ocrResultId, checkRecordId),
            OcrAchExtensionJson = CheckAchExtensionData.Serialize(ocrAch),
            CorrectAchExtensionJson = CheckAchExtensionData.Serialize(submittedAch),
            OcrCheckTemplateId = ocrCheckTemplateId
        };
    }

    private static bool HasStructuredDiff(
        OcrResultDto ocr,
        string correctCheckNumber,
        decimal correctAmount,
        DateTime correctDateUtc,
        CheckAchExtensionData ocrAch,
        CheckAchExtensionData correctAch)
    {
        if (!string.Equals(NormCheckNumber(ocr.CheckNumber), NormCheckNumber(correctCheckNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        if (ocr.Amount != correctAmount)
            return true;
        if (ocr.Date.Date != correctDateUtc.Date)
            return true;

        // 表单未采集 MicrFieldOrderNote 时视为沿用 OCR 值参与比较，避免伪差异。
        var correctForDiff = string.IsNullOrWhiteSpace(correctAch.MicrFieldOrderNote)
            ? correctAch with { MicrFieldOrderNote = ocrAch.MicrFieldOrderNote }
            : correctAch;

        return !CheckAchExtensionData.EqualsForTraining(ocrAch, correctForDiff);
    }

    private static string NormCheckNumber(string? v) => string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim();
}
