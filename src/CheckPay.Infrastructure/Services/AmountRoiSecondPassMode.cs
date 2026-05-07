namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 金额 ROI 二次 Vision Read 的触发策略（与 <c>Ocr:AmountRoiSecondPass:Mode</c> 对齐）。
/// </summary>
internal enum AmountRoiSecondPassMode
{
    /// <summary>仅在置信不足、疑似数字粘连或大写与数字严重背离时触发（默认）。</summary>
    OnDemand,

    /// <summary>只要算出 ROI 边界即再跑一次 Read（每张票多耗一次 Vision；成本不敏感时抬成功率）。</summary>
    Always
}
