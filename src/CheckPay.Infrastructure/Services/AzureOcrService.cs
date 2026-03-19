using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using CheckPay.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CheckPay.Infrastructure.Services;

public class AzureOcrService : IOcrService
{
    private readonly DocumentAnalysisClient _client;

    public AzureOcrService(IConfiguration configuration)
    {
        var endpoint = configuration["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("Azure Document Intelligence Endpoint未配置");
        var apiKey = configuration["Azure:DocumentIntelligence:ApiKey"]
            ?? throw new InvalidOperationException("Azure Document Intelligence ApiKey未配置");

        _client = new DocumentAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        var operation = await _client.AnalyzeDocumentFromUriAsync(
            WaitUntil.Completed,
            "prebuilt-check",
            new Uri(imageUrl),
            cancellationToken: cancellationToken);

        var result = operation.Value;
        var document = result.Documents.FirstOrDefault()
            ?? throw new InvalidOperationException("未能识别支票信息");

        var checkNumber = GetFieldValue(document, "CheckNumber");
        var amount = GetFieldValue(document, "Amount");
        var date = GetFieldValue(document, "Date");

        var confidenceScores = new Dictionary<string, double>
        {
            { "CheckNumber", GetFieldConfidence(document, "CheckNumber") },
            { "Amount", GetFieldConfidence(document, "Amount") },
            { "Date", GetFieldConfidence(document, "Date") }
        };

        return new OcrResultDto(
            CheckNumber: checkNumber,
            Amount: decimal.TryParse(amount, out var amt) ? amt : 0m,
            Date: DateTime.TryParse(date, out var dt) ? dt : DateTime.UtcNow,
            ConfidenceScores: confidenceScores
        );
    }

    private static string GetFieldValue(AnalyzedDocument document, string fieldName)
    {
        return document.Fields.TryGetValue(fieldName, out var field) && field.Content != null
            ? field.Content
            : string.Empty;
    }

    private static double GetFieldConfidence(AnalyzedDocument document, string fieldName)
    {
        return document.Fields.TryGetValue(fieldName, out var field) && field.Confidence.HasValue
            ? (double)field.Confidence.Value
            : 0.0;
    }

    /// <summary>
    /// AzureOcrService 当前仅用于支票识别（prebuilt-check 模型）。
    /// 扣款凭证 OCR 由 HunyuanOcrService 处理，此处返回空结果交由财务手动填写。
    /// </summary>
    public Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        var empty = new DebitOcrResultDto(
            CheckNumber: null,
            Amount: null,
            Date: null,
            BankReference: null,
            ConfidenceScores: new Dictionary<string, double>()
        );
        return Task.FromResult(empty);
    }
}
