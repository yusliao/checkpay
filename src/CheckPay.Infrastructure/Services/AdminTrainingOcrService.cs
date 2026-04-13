using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 训练标注页 OCR：优先 Azure Vision Read（与生产主流程 IOcrService 解耦）。
/// </summary>
public sealed class AdminTrainingOcrService(IOcrService primary) : IAdminTrainingOcrService
{
    public Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
        => primary.ProcessCheckImageAsync(imageUrl, cancellationToken);

    public Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
        => primary.ProcessDebitImageAsync(imageUrl, cancellationToken);
}
