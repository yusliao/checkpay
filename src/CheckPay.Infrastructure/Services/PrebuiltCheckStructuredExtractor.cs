using System.Text.RegularExpressions;
using Azure.AI.DocumentIntelligence;

namespace CheckPay.Infrastructure.Services;

/// <summary>从 Document Intelligence <c>prebuilt-check.us</c> 的 <see cref="AnalyzeResult"/> 抽取结构化字段（与 Vision Read 结果融合）。</summary>
internal static class PrebuiltCheckStructuredExtractor
{
    private static readonly Regex NonDigits = new(@"\D+", RegexOptions.Compiled);

    public static PrebuiltCheckStructuredFields TryExtract(AnalyzeResult? result)
    {
        if (result?.Documents is null || result.Documents.Count == 0)
            return PrebuiltCheckStructuredFields.Empty;

        var doc = result.Documents[0];
        var f = new PrebuiltCheckStructuredFields();

        TryFillMicrNested(doc, f);
        f.BankName = GetPlainString(doc, "BankName");
        f.PayTo = GetPlainString(doc, "PayTo");
        f.PayerName = GetPlainString(doc, "PayerName");
        f.PayerAddress = GetPlainString(doc, "PayerAddress");
        f.NumberAmount = GetCurrencyOrNumber(doc, "NumberAmount");
        f.NumberAmountConfidence = GetFieldConfidence(doc, "NumberAmount");
        f.CheckDate = GetDateField(doc, "CheckDate");
        f.CheckDateConfidence = GetFieldConfidence(doc, "CheckDate");

        return f;
    }

    private static void TryFillMicrNested(AnalyzedDocument doc, PrebuiltCheckStructuredFields target)
    {
        if (!doc.Fields.TryGetValue("MICR", out var micr) || micr?.ValueDictionary is not { Count: > 0 } dict)
            return;

        if (dict.TryGetValue("RoutingNumber", out var rt))
        {
            target.RoutingNumber = ToDigits(rt);
            target.RoutingConfidence = Convert.ToDouble(rt.Confidence ?? 0f);
        }

        if (dict.TryGetValue("AccountNumber", out var ac))
        {
            target.AccountNumber = ToDigits(ac);
            target.AccountConfidence = Convert.ToDouble(ac.Confidence ?? 0f);
        }

        if (dict.TryGetValue("CheckNumber", out var cn))
        {
            target.CheckNumberMicr = ToDigits(cn);
            target.CheckNumberConfidence = Convert.ToDouble(cn.Confidence ?? 0f);
        }
    }

    private static string? GetPlainString(AnalyzedDocument doc, string key) =>
        doc.Fields.TryGetValue(key, out var field) ? ReadStringField(field) : null;

    private static string? ReadStringField(DocumentField? field)
    {
        if (field is null)
            return null;

        if (field.FieldType == DocumentFieldType.String && !string.IsNullOrWhiteSpace(field.ValueString))
            return field.ValueString.Trim();

        return string.IsNullOrWhiteSpace(field.Content) ? null : field.Content.Trim();
    }

    private static string? ToDigits(DocumentField field)
    {
        var s = field.FieldType == DocumentFieldType.String ? field.ValueString : field.Content;
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var d = NonDigits.Replace(s, string.Empty);
        return d.Length > 0 ? d : null;
    }

    private static double GetFieldConfidence(AnalyzedDocument doc, string key) =>
        doc.Fields.TryGetValue(key, out var field) && field?.Confidence is { } c ? Convert.ToDouble(c) : 0.0;

    private static decimal? GetCurrencyOrNumber(AnalyzedDocument doc, string key)
    {
        if (!doc.Fields.TryGetValue(key, out var field) || field is null)
            return null;

        if (field.FieldType == DocumentFieldType.Currency && field.ValueCurrency is { } cur)
            return Convert.ToDecimal(cur.Amount);

        if (field.FieldType == DocumentFieldType.Double && field.ValueDouble is { } d)
            return Convert.ToDecimal(d);

        if (field.FieldType == DocumentFieldType.Int64 && field.ValueInt64 is { } i)
            return i;

        return null;
    }

    private static DateTime? GetDateField(AnalyzedDocument doc, string key)
    {
        if (!doc.Fields.TryGetValue(key, out var field) || field is null)
            return null;

        if (field.FieldType == DocumentFieldType.Date && field.ValueDate is { } dto)
            return dto.UtcDateTime;

        if (DateTime.TryParse(field.Content, out var parsed))
            return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);

        return null;
    }
}

internal sealed class PrebuiltCheckStructuredFields
{
    public static PrebuiltCheckStructuredFields Empty { get; } = new();

    public string? RoutingNumber { get; set; }
    public double RoutingConfidence { get; set; }
    public string? AccountNumber { get; set; }
    public double AccountConfidence { get; set; }
    public string? CheckNumberMicr { get; set; }
    public double CheckNumberConfidence { get; set; }
    public string? BankName { get; set; }
    public string? PayTo { get; set; }
    public string? PayerName { get; set; }
    public string? PayerAddress { get; set; }
    public decimal? NumberAmount { get; set; }
    public double NumberAmountConfidence { get; set; }
    public DateTime? CheckDate { get; set; }
    public double CheckDateConfidence { get; set; }
}
