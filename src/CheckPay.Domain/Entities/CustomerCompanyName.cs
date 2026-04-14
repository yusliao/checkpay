using CheckPay.Domain.Common;

namespace CheckPay.Domain.Entities;

/// <summary>客户编号下的关联公司名称（一对多：一个客户可对应多个开票/付款主体名称）。</summary>
public class CustomerCompanyName : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>公司名称（与支票票面或合同主体一致，trim 后入库）</summary>
    public string CompanyName { get; set; } = string.Empty;
}
