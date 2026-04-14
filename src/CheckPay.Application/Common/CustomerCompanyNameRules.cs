namespace CheckPay.Application.Common;

/// <summary>客户主数据「公司名称」与支票录入比对规则。</summary>
public static class CustomerCompanyNameRules
{
    public static string NormalizeKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>未填写公司名称视为无需比对；否则须在登记列表中存在（忽略大小写、首尾空格）。</summary>
    public static bool IsRegisteredCompany(IEnumerable<string?> registeredNames, string? enteredName)
    {
        if (string.IsNullOrWhiteSpace(enteredName)) return true;
        var key = NormalizeKey(enteredName);
        return registeredNames.Any(r => NormalizeKey(r) == key);
    }
}
