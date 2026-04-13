using CheckPay.Domain.Common;

namespace CheckPay.Domain.Entities;

public class Customer : BaseEntity
{
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>期望开户行名称（与 OCR 比对，可空表示不校验）</summary>
    public string? ExpectedBankName { get; set; }

    /// <summary>期望账户持有人名称（与 OCR 比对，可空表示不校验）</summary>
    public string? ExpectedAccountHolderName { get; set; }
}
