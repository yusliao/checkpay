namespace CheckPay.Web.Common;

public static class OcrAmountValidationNotes
{
    public const string MismatchDraftMarker = "[系统提示] 手写金额与数字金额不一致，已转草稿待人工复核。";
    public const string ManualReviewCompleteMarker = "[人工复核] 金额大小写差异已人工确认可提交。";
    public const string UiDateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    public const string MismatchBlockingMessage = "已检测到手写金额与数字金额不一致：禁止提交入库，仅可保存草稿并人工复核。";
    public const string ManualReviewCompletedMessage = "已人工复核并确认差异为误报，当前允许提交入库。";
    public const string MismatchSubmitBlockedReason = "手写金额与数字金额不一致，当前仅允许保存草稿，请人工复核后再提交";
    public const string ManualValidateButtonText = "手动校验手写金额";
    public const string ManualValidatingButtonText = "金额校验中...";
    public const string ManualReviewCompleteButtonText = "人工复核完毕";
    public const string DraftOnMismatchButtonText = "一键转草稿（金额不一致）";
}
