namespace CheckPay.Application.Common.Interfaces;

public interface IOcrService
{
    Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default);
}

public record OcrResultDto(
    string CheckNumber,
    decimal Amount,
    DateTime Date,
    Dictionary<string, double> ConfidenceScores
);
