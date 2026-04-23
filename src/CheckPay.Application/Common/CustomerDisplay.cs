namespace CheckPay.Application.Common;

/// <summary>客户相关字段在 UI/导出中的展示约定。</summary>
public static class CustomerDisplay
{
    public static string MobileOrDash(string? mobile) =>
        string.IsNullOrWhiteSpace(mobile) ? "—" : mobile.Trim();
}
