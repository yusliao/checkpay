namespace CheckPay.Application.Common.Interfaces;

public interface IOcrService
{
    Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default);
    Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default);
}

public record OcrResultDto(
    string CheckNumber,
    decimal Amount,
    DateTime Date,
    Dictionary<string, double> ConfidenceScores
);

/// <summary>
/// 扣款凭证 OCR 识别结果，所有字段可空（识别失败时为 null，由财务手动填写）
/// </summary>
public record DebitOcrResultDto(
    string? CheckNumber,
    decimal? Amount,
    DateTime? Date,
    string? BankReference,
    Dictionary<string, double> ConfidenceScores
);
