namespace CheckPay.Application.Common;

/// <summary>OCR 票面客户相关字段：公司名、持有人、Pay to 合并为支票上应对齐的同一显示名。</summary>
public static class OcrCheckCustomerFields
{
    public static string? MergeHolderCompanyDisplayName(string? companyName, string? accountHolderName, string? payToOrderOf)
    {
        if (!string.IsNullOrWhiteSpace(companyName)) return companyName.Trim();
        if (!string.IsNullOrWhiteSpace(accountHolderName)) return accountHolderName.Trim();
        if (!string.IsNullOrWhiteSpace(payToOrderOf)) return payToOrderOf.Trim();
        return null;
    }
}
