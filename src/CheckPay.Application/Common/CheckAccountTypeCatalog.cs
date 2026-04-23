namespace CheckPay.Application.Common;

/// <summary>支票 ACH 录入中「Account Type」销售可选值及 OCR 文本归一化。</summary>
public static class CheckAccountTypeCatalog
{
    public const string BusinessChecking = "Business Checking";
    public const string Savings = "Savings";

    /// <summary>将 OCR 或自由文本映射为销售下拉选项；无法可靠映射时返回 null（由用户手动选择）。</summary>
    public static string? MatchSalesSelectable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Equals(BusinessChecking, StringComparison.OrdinalIgnoreCase)) return BusinessChecking;
        if (t.Equals(Savings, StringComparison.OrdinalIgnoreCase)) return Savings;
        if (t.Contains("saving", StringComparison.OrdinalIgnoreCase)) return Savings;
        if (t.Contains("checking", StringComparison.OrdinalIgnoreCase)) return BusinessChecking;
        if (t.Contains("business", StringComparison.OrdinalIgnoreCase)) return BusinessChecking;
        return null;
    }
}
