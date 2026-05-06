using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

/// <summary>训练标注页 OCR：委托生产用 <see cref="IOcrService"/>。</summary>
public sealed class AdminTrainingOcrService(IOcrService primary) : IAdminTrainingOcrService
{
    public Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
        => primary.ProcessCheckImageAsync(imageUrl, cancellationToken);

    public Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
        => primary.ProcessDebitImageAsync(imageUrl, cancellationToken);

    public Task<AmountValidationResult> ValidateHandwrittenAmountAsync(
        string imageUrl,
        decimal numericAmount,
        CancellationToken cancellationToken = default,
        string? companionFullTextForLegalAmount = null)
        => primary.ValidateHandwrittenAmountAsync(imageUrl, numericAmount, cancellationToken, companionFullTextForLegalAmount);
}
