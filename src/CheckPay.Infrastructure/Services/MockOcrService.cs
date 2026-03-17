using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

public class MockOcrService : IOcrService
{
    public Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        // Mock OCR结果，模拟识别成功
        var confidenceScores = new Dictionary<string, double>
        {
            { "CheckNumber", 0.95 },
            { "Amount", 0.92 },
            { "Date", 0.88 }
        };

        var result = new OcrResultDto(
            CheckNumber: "MOCK-" + Random.Shared.Next(10000, 99999),
            Amount: Random.Shared.Next(100, 10000),
            Date: DateTime.Today.AddDays(-Random.Shared.Next(1, 30)),
            ConfidenceScores: confidenceScores
        );

        return Task.FromResult(result);
    }
}
