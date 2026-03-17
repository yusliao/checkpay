namespace CheckPay.Domain.Enums;

public enum CheckStatus
{
    PendingDebit,    // 待扣款
    PendingReview,   // 待核查
    Confirmed,       // 已确认
    Questioned       // 存疑
}
