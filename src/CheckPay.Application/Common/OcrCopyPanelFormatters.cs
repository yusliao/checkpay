using System.Text;
using System.Text.Json;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;

namespace CheckPay.Application.Common;

/// <summary>上传/导入页「抽取全文 / 解析摘要」展示文本，与训练标注页格式一致，便于复制。</summary>
public static class OcrCheckCopyPanelFormatter
{
    public static string FromJsonDocument(JsonDocument? doc)
    {
        if (doc == null) return string.Empty;
        try
        {
            var dto = JsonSerializer.Deserialize<OcrResultDto>(doc.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto != null)
                return Build(dto);
        }
        catch
        {
            // ignore
        }

        try
        {
            return "// OCR JSON（无法解析为结构化结果）\n" +
                   JsonSerializer.Serialize(doc.RootElement,
 new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string Build(OcrResultDto result)
    {
        var ach = CheckAchExtensionData.FromOcrResult(result);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.ExtractedText))
        {
            sb.AppendLine("// Azure Vision Read — 抽取全文");
            sb.AppendLine(result.ExtractedText);
            sb.AppendLine();
        }

        sb.AppendLine("// 支票 OCR 解析摘要");
        sb.AppendLine("{");
        sb.AppendLine($"  \"check_number\": {JsonVal(string.IsNullOrWhiteSpace(result.CheckNumber) ? null : result.CheckNumber)},");
        sb.AppendLine($"  \"amount\": {result.Amount:F2},");
        sb.AppendLine($"  \"date\": {JsonVal(result.Date.ToString("yyyy-MM-dd"))},");
        sb.AppendLine($"  \"routing_number\": {JsonVal(ach.RoutingNumber)},");
        sb.AppendLine($"  \"account_number\": {JsonVal(ach.AccountNumber)},");
        sb.AppendLine($"  \"bank_name\": {JsonVal(ach.BankName)},");
        sb.AppendLine($"  \"company_name\": {JsonVal(ach.CompanyName)},");
        sb.AppendLine($"  \"check_number_micr\": {JsonVal(ach.CheckNumberMicr)},");
        sb.AppendLine($"  \"micr_line_raw\": {JsonVal(ach.MicrLineRaw)},");
        AppendDiagnosticsHandwrittenSnippet(sb, result.Diagnostics);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendDiagnosticsHandwrittenSnippet(StringBuilder sb, IReadOnlyDictionary<string, string>? diagnostics)
    {
        if (diagnostics is null)
            return;

        AppendDiagnosticPropertyLine(sb, diagnostics, "di_amount_validation_status");
        AppendDiagnosticPropertyLine(sb, diagnostics, "di_handwritten_di_service_status");
        AppendDiagnosticPropertyLine(sb, diagnostics, "di_handwritten_legal_amount_raw");
        AppendDiagnosticPropertyLine(sb, diagnostics, "di_handwritten_legal_amount_parsed");
        AppendDiagnosticPropertyLine(sb, diagnostics, "di_handwritten_validation_confidence");
        AppendDiagnosticPropertyLine(sb, diagnostics, "di_handwritten_is_consistent");
        AppendDiagnosticPropertyLine(sb, diagnostics, "di_handwritten_validation_reason");
    }

    private static void AppendDiagnosticPropertyLine(
        StringBuilder sb,
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        if (!diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return;
        sb.AppendLine($"  \"{key}\": {JsonVal(value)},");
    }

    private static string JsonVal(string? v) => v == null ? "null" : $"\"{v}\"";
}

public static class OcrDebitCopyPanelFormatter
{
    public static string Build(DebitOcrResultDto result)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.RawExtractedText))
        {
            sb.AppendLine("// Azure Vision Read — 抽取全文");
            sb.AppendLine(result.RawExtractedText);
            sb.AppendLine();
        }

        sb.AppendLine("// 扣款凭证 OCR 解析摘要");
        sb.AppendLine("{");
        sb.AppendLine($"  \"check_number\": {JsonVal(result.CheckNumber)},");
        sb.AppendLine($"  \"amount\": {JsonVal(result.Amount)},");
        sb.AppendLine($"  \"date\": {JsonVal(result.Date?.ToString("yyyy-MM-dd"))},");
        sb.AppendLine($"  \"bank_reference\": {JsonVal(result.BankReference)}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string JsonVal(string? v) => v == null ? "null" : $"\"{v}\"";
    private static string JsonVal(decimal? v) => v == null ? "null" : v.Value.ToString("F2");
}
