using CheckPay.Domain.Common;
using CheckPay.Domain.Enums;

namespace CheckPay.Domain.Entities;

public class CheckRecord : BaseEntity
{
    public string CheckNumber { get; set; } = string.Empty;
    public decimal CheckAmount { get; set; }
    public DateTime CheckDate { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public CheckStatus Status { get; set; } = CheckStatus.PendingDebit;
    public string? ImageUrl { get; set; }
    public Guid? OcrResultId { get; set; }
    public OcrResult? OcrResult { get; set; }
    public string? Notes { get; set; }
    public uint RowVersion { get; set; }
    // 反向导航：关联的扣款记录
    public DebitRecord? DebitRecord { get; set; }
}
