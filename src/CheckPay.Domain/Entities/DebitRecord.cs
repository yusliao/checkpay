using CheckPay.Domain.Common;
using CheckPay.Domain.Enums;

namespace CheckPay.Domain.Entities;

public class DebitRecord : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string CheckNumber { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public DateTime DebitDate { get; set; }
    public string BankReference { get; set; } = string.Empty;
    public DebitStatus DebitStatus { get; set; } = DebitStatus.Unmatched;
    public string? ScanImageUrl { get; set; }
    public Guid? CheckRecordId { get; set; }
    public CheckRecord? CheckRecord { get; set; }
    public string? Notes { get; set; }
    public uint RowVersion { get; set; }
}
