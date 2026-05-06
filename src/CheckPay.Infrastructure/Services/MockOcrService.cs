using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

public class MockOcrService : IOcrService
{
    public Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        var confidenceScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "CheckNumber", 0.95 },
            { "CheckNumberMicr", 0.90 },
            { "Amount", 0.92 },
            { "Date", 0.88 },
            { "RoutingNumber", 0.91 },
            { "AccountNumber", 0.89 },
            { "BankName", 0.87 },
            { "AccountHolderName", 0.86 },
            { "AccountAddress", 0.84 },
            { "AccountType", 0.85 },
            { "PayToOrderOf", 0.40 },
            { "CompanyName", 0.82 },
            { "ForMemo", 0.35 },
            { "MicrLineRaw", 0.72 }
        };

        var result = new OcrResultDto(
            CheckNumber: "MOCK-" + Random.Shared.Next(10000, 99999),
            Amount: Random.Shared.Next(100, 10000),
            Date: DateTime.Today.AddDays(-Random.Shared.Next(1, 30)),
            ConfidenceScores: confidenceScores,
            RoutingNumber: $"{Random.Shared.Next(100000000, 999999999):D9}",
            AccountNumber: $"{Random.Shared.Next(100000000, 999999999)}{Random.Shared.Next(10, 99)}",
            BankName: "MOCK FIRST HORIZON BANK",
            AccountHolderName: "MOCK YUMTN INC",
            AccountAddress: "1735 W MOCK RD STE 8, JOHNSON CITY, TN 37604",
            AccountType: "Business Checking",
            PayToOrderOf: "MOCK PAYEE LLC",
            ForMemo: null,
            MicrLineRaw: "MOCK MICR 064201450 22000378552 2944",
            CheckNumberMicr: "2944",
            MicrFieldOrderNote: null,
            CompanyName: "MOCK PAYEE LLC");

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

    public Task<AmountValidationResult> ValidateHandwrittenAmountAsync(
        string imageUrl,
        decimal numericAmount,
        CancellationToken cancellationToken = default,
        string? companionFullTextForLegalAmount = null)
    {
        var legalAmount = Math.Round(numericAmount, 2);
        var outcome = new AmountValidationResult(
            NumericAmount: numericAmount,
            LegalAmountParsed: legalAmount,
            LegalAmountRaw: $"{legalAmount:N2} DOLLARS",
            IsConsistent: true,
            Confidence: 0.5,
            Status: "completed",
            Reason: "Mock OCR 固定返回一致");
        return Task.FromResult(outcome);
    }
}
