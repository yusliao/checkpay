namespace CheckPay.Domain.Enums;

public enum DebitStatus
{
    Matched,      // 已匹配
    Unmatched,    // 未匹配
    PendingReview // 待核查
}
