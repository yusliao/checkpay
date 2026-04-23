using CheckPay.Domain.Common;

namespace CheckPay.Domain.Entities;

public class Customer : BaseEntity
{
    /// <summary>客户主键业务号：存支票 OCR 的账号 (AccountNumber)，与票面账号一致。</summary>
    public string CustomerCode { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    /// <summary>客户手机号（业务必填）。同一手机号可对应多个客户账号（1 对多）；每个客户账号下可有多个关联公司名称（1 对多）。</summary>
    public string MobilePhone { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>false=未授权（如 OCR 自动建档）；true=已在客户管理中确认授权。</summary>
    public bool IsAuthorized { get; set; } = false;

    /// <summary>期望开户行名称（与 OCR 比对，可空表示不校验）</summary>
    public string? ExpectedBankName { get; set; }

    /// <summary>票面公司名称（与 OCR AccountHolderName / CompanyName 比对，二者业务上同义，可空表示不校验）</summary>
    public string? ExpectedAccountHolderName { get; set; }

    /// <summary>与 <see cref="ExpectedAccountHolderName"/> 同值，对应 OCR CompanyName 字段。</summary>
    public string? ExpectedCompanyName { get; set; }

    /// <summary>期望账户地址（与 OCR AccountAddress 比对，可空表示不校验）</summary>
    public string? ExpectedAccountAddress { get; set; }

    public ICollection<CustomerCompanyName> CompanyNames { get; set; } = new List<CustomerCompanyName>();
}
