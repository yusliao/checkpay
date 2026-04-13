namespace CheckPay.Application.Common.Interfaces;

/// <summary>
/// 从支票 OCR 训练样本生成混元 Prompt 纠偏段落（动态少样本）。
/// </summary>
public interface ICheckOcrFewShotProvider
{
    /// <summary>
    /// 生成追加到支票 OCR 用户 Prompt 末尾的文本；无样本或禁用时返回空字符串。
    /// </summary>
    Task<string> BuildCheckPromptAugmentationAsync(CancellationToken cancellationToken = default);
}
