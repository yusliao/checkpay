using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

public class MockOcrService : IOcrService
{
    public Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
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

    public Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        var confidenceScores = new Dictionary<string, double>
        {
            { "CheckNumber", 0.80 },
            { "Amount", 0.90 },
            { "Date", 0.85 },
            { "BankReference", 0.88 }
        };

        var result = new DebitOcrResultDto(
            CheckNumber: "MOCK-" + Random.Shared.Next(10000, 99999),
            Amount: Random.Shared.Next(100, 10000),
            Date: DateTime.Today.AddDays(-Random.Shared.Next(1, 10)),
            BankReference: "TRC" + Random.Shared.Next(100000000, 999999999),
            ConfidenceScores: confidenceScores
        );

        return Task.FromResult(result);
    }
}
