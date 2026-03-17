using CheckPay.Domain.Common;

namespace CheckPay.Domain.Entities;

public class Customer : BaseEntity
{
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
