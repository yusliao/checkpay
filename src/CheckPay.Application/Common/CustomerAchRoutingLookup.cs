namespace CheckPay.Application.Common;

/// <summary>客户主数据与支票表单上的「账号 + ABA」对齐规则。</summary>
public static class CustomerAchRoutingLookup
{
    /// <summary>
    /// 从表单路由字段得到与客户表一致的路由键：仅保留数字且恰好 9 位时返回，否则返回空字符串（不按银行区分）。
    /// </summary>
    public static string NormalizeRoutingKey(string? routingFromForm)
    {
        if (string.IsNullOrWhiteSpace(routingFromForm))
            return string.Empty;
        var digits = string.Concat(routingFromForm.Where(char.IsDigit));
        return digits.Length == 9 ? digits : string.Empty;
    }
}
