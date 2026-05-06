using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Enums;

namespace CheckPay.Application.Common;

/// <summary>
/// 将 Worker / 页面「手写金额校验」的 DI 结果合并进 <c>raw_result</c> 的 <c>Diagnostics</c>，便于复制面板与复盘。
/// </summary>
public static class OcrRawResultAmountValidationDiagnostics
{
    private static readonly JsonSerializerOptions DeserializeValidationOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 基于当前 <paramref name="rawPayload"/> 克隆并写入 <c>di_handwritten_*</c> / <c>di_amount_validation_*</c>；不释放入参。
    /// </summary>
    public static JsonDocument MergeIntoRawJson(
        JsonDocument rawPayload,
        JsonDocument? amountValidationJson,
        AmountValidationStatus status,
        string? amountValidationErrorMessage)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(rawPayload.RootElement.GetRawText())!;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(rawPayload.RootElement.GetRawText());
        }

        var diag = root["Diagnostics"] as JsonObject ?? new JsonObject();
        root["Diagnostics"] = diag;

        AmountValidationResult? v = null;
        if (amountValidationJson != null)
        {
            try
            {
                v = JsonSerializer.Deserialize<AmountValidationResult>(
                    amountValidationJson.RootElement.GetRawText(),
                    DeserializeValidationOptions);
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        diag["di_amount_validation_status"] = status.ToString();

        if (v != null)
        {
            diag["di_handwritten_di_service_status"] = v.Status;
            diag["di_handwritten_legal_amount_raw"] = v.LegalAmountRaw ?? string.Empty;
            diag["di_handwritten_legal_amount_parsed"] =
                v.LegalAmountParsed?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;
            diag["di_handwritten_validation_confidence"] =
                v.Confidence.ToString("F4", CultureInfo.InvariantCulture);
            diag["di_handwritten_is_consistent"] = v.IsConsistent?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(v.Reason))
                diag["di_handwritten_validation_reason"] = v.Reason;
        }
        else if (!string.IsNullOrEmpty(amountValidationErrorMessage)
                 && status is AmountValidationStatus.Skipped or AmountValidationStatus.Failed)
        {
            diag["di_handwritten_validation_reason"] = amountValidationErrorMessage;
        }

        return JsonDocument.Parse(root.ToJsonString());
    }
}
