using System.Text.RegularExpressions;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.AI.Vision.ImageAnalysis;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// Azure AI Vision OCR 服务（使用 Read API 提取文字，正则解析支票字段）
/// 注意：Azure AI Vision 是通用 OCR，无 prebuilt-check 模型，需自行解析文字
/// </summary>
public class AzureOcrService : IOcrService
{
    private readonly ImageAnalysisClient _client;
    private readonly DocumentAnalysisClient _documentClient;
    private readonly IBlobStorageService _blobStorageService;
    private readonly ICheckOcrTemplateResolver _templateResolver;
    private readonly ICheckOcrParsedSampleCorrector _parsedSampleCorrector;
    private readonly ILogger<AzureOcrService> _logger;

    public AzureOcrService(
        IConfiguration configuration,
        ILogger<AzureOcrService> logger,
        IBlobStorageService blobStorageService,
        ICheckOcrTemplateResolver templateResolver,
        ICheckOcrParsedSampleCorrector parsedSampleCorrector)
    {
        var endpoint = configuration["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("Azure AI Vision Endpoint 未配置");
        var apiKey = configuration["Azure:DocumentIntelligence:ApiKey"]
            ?? throw new InvalidOperationException("Azure AI Vision ApiKey 未配置");

        _client = new ImageAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _documentClient = new DocumentAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _logger = logger;
        _blobStorageService = blobStorageService;
        _templateResolver = templateResolver;
        _parsedSampleCorrector = parsedSampleCorrector;
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理支票: {ImageUrl}", imageUrl);

        var layout = await GetReadLayoutAsync(imageUrl, cancellationToken);
        var rawText = layout.FullText;
        _logger.LogInformation("Azure Vision OCR 提取原始文字: {RawText}", rawText);

        var micrHintForRouting = layout.ConcatLinesInRegion(CheckOcrParsingProfile.Default.MicrPriorRegion, "\n");
        if (string.IsNullOrWhiteSpace(micrHintForRouting))
            micrHintForRouting = rawText;

        var routingHint = CheckOcrVisionReadParser.ParseMicrHeuristic(micrHintForRouting).routing;
        var resolution = await _templateResolver.ResolveAsync(routingHint, rawText, cancellationToken);
        var profile = resolution.Profile;

        _logger.LogInformation(
            "Azure Vision OCR 票型解析 TemplateId={TemplateId} TemplateName={TemplateName}",
            resolution.TemplateId,
            resolution.TemplateName);

        var (checkNumber, checkConf) = CheckOcrVisionReadParser.ParseCheckNumber(layout, profile);
        var (amount, amountConf) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
        var (date, dateConf) = CheckOcrVisionReadParser.ParseDate(layout, profile);

        _logger.LogInformation(
            "Azure Vision OCR 解析结果 — 支票号: {CheckNumber}({ConfCN:F2}) | 金额: {Amount}({ConfAmt:F2}) | 日期: {Date}({ConfDate:F2})",
            checkNumber, checkConf, amount, amountConf, date?.ToString("yyyy-MM-dd"), dateConf);

        var micrBlock = layout.ConcatLinesInRegion(profile.MicrPriorRegion, "\n");
        var micrParseText = string.IsNullOrWhiteSpace(micrBlock) ? rawText : micrBlock;
        var (routing, acct, micrLine, rtConf, acConf, micConf) =
            CheckOcrVisionReadParser.ParseMicrHeuristic(micrParseText);
        var (payToLine, payToConf) = CheckOcrVisionReadParser.ParsePayToOrderLine(rawText);

        var conf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "CheckNumber", checkConf },
            { "CheckNumberMicr", micConf },
            { "Amount", amountConf },
            { "Date", dateConf },
            { "RoutingNumber", rtConf },
            { "AccountNumber", acConf },
            { "BankName", 0.1 },
            { "AccountHolderName", 0.1 },
            { "AccountAddress", 0.1 },
            { "AccountType", 0.1 },
            { "PayToOrderOf", payToConf },
            { "CompanyName", payToConf },
            { "ForMemo", 0.1 },
            { "MicrLineRaw", micConf }
        };

        var dto = new OcrResultDto(
            CheckNumber: checkNumber,
            Amount: amount,
            Date: date ?? DateTime.UtcNow,
            ConfidenceScores: conf,
            RoutingNumber: routing,
            AccountNumber: acct,
            PayToOrderOf: payToLine,
            CompanyName: payToLine,
            MicrLineRaw: micrLine,
            ExtractedText: rawText);

        return await _parsedSampleCorrector.ApplyIfMatchedAsync(dto, cancellationToken);
    }

    private async Task<ReadOcrLayout> GetReadLayoutAsync(string imageUrl, CancellationToken cancellationToken)
    {
        using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;
        _logger.LogInformation("Azure Vision 图片下载完成，大小: {Size} bytes", memoryStream.Length);

        var analysisResult = await _client.AnalyzeAsync(
            BinaryData.FromStream(memoryStream),
            VisualFeatures.Read,
            cancellationToken: cancellationToken);

        return AzureReadLayoutExtractor.From(analysisResult.Value);
    }

    /// <summary>
    /// 扣款凭证：使用 Read API 抽全文 + 与支票类似的启发式字段解析（训练标注与管理端预览）。
    /// </summary>
    public async Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理扣款凭证: {ImageUrl}", imageUrl);
        var layout = await GetReadLayoutAsync(imageUrl, cancellationToken);
        var rawText = layout.FullText;
        _logger.LogInformation("Azure Vision OCR 扣款凭证全文: {RawText}", rawText);

        var profile = CheckOcrParsingProfile.Default;
        var (checkStr, checkConf) = CheckOcrVisionReadParser.ParseCheckNumber(layout, profile);
        var checkNumber = string.IsNullOrWhiteSpace(checkStr) ? null : checkStr.Trim();

        var (amountVal, amountConf) = CheckOcrVisionReadParser.ParseAmount(layout, profile);
        decimal? amount = amountVal > 0m ? amountVal : null;
        if (amount is null)
            amountConf = 0.12;

        var (dateVal, dateConf) = CheckOcrVisionReadParser.ParseDate(layout, profile);

        var (bankRef, refConf) = CheckOcrVisionReadParser.ParseBankReference(rawText);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheckNumber"] = checkNumber is null ? 0.12 : checkConf,
            ["Amount"] = amount is null ? 0.12 : amountConf,
            ["Date"] = dateVal is null ? 0.12 : dateConf,
            ["BankReference"] = bankRef is null ? 0.12 : refConf
        };

        return new DebitOcrResultDto(checkNumber, amount, dateVal, bankRef, scores, RawExtractedText: rawText);
    }

    public async Task<AmountValidationResult> ValidateHandwrittenAmountAsync(
        string imageUrl,
        decimal numericAmount,
        CancellationToken cancellationToken = default)
    {
        if (numericAmount <= 0m)
            return new AmountValidationResult(numericAmount, null, null, null, 0.0, "skipped", "数字金额无效，跳过校验");

        using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        AnalyzeDocumentOperation operation = await _documentClient.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            "prebuilt-check",
            memoryStream,
            cancellationToken: cancellationToken);

        var result = operation.Value;
        var (legalRaw, legalAmount, confidence, reason) = ExtractLegalAmount(result);
        if (legalAmount is null)
        {
            return new AmountValidationResult(
                numericAmount,
                null,
                legalRaw,
                null,
                confidence,
                "failed",
                reason ?? "未识别到可解析的手写金额");
        }

        var consistent = Math.Abs(legalAmount.Value - numericAmount) <= 0.01m;
        return new AmountValidationResult(
            numericAmount,
            legalAmount,
            legalRaw,
            consistent,
            confidence,
            "completed",
            consistent ? "手写金额与数字金额一致" : "手写金额与数字金额不一致");
    }

    private static (string? rawText, decimal? amount, double confidence, string? reason) ExtractLegalAmount(AnalyzeResult result)
    {
        if (result.Documents.Count == 0)
            return (null, null, 0.0, "Document Intelligence 未返回文档结构");

        var doc = result.Documents[0];
        var candidateKeys = new[]
        {
            "AmountInWords", "AmountWritten", "LegalAmount", "Amount", "PayToTheOrderOf"
        };

        foreach (var key in candidateKeys)
        {
            if (!doc.Fields.TryGetValue(key, out var field) || field is null)
                continue;

            var confidence = Convert.ToDouble(field.Confidence ?? 0f);
            if (TryGetAmountFromField(field, out var amount, out var raw))
            {
                return (raw, amount, confidence, null);
            }
        }

        var line = result.Content?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => x.Contains("dollar", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(line) && TryParseAmountFromWords(line, out var parsed))
            return (line, parsed, 0.35, "从全文 Dollar 行启发式解析");

        return (line, null, 0.1, "未找到可解析的手写金额字段");
    }

    private static bool TryGetAmountFromField(DocumentField field, out decimal amount, out string raw)
    {
        raw = field.Content ?? string.Empty;
        amount = 0m;

        if (field.FieldType == DocumentFieldType.Currency)
        {
            amount = Convert.ToDecimal(field.Value.AsCurrency().Amount);
            raw = field.Content ?? amount.ToString("N2");
            return amount > 0;
        }

        if (field.FieldType == DocumentFieldType.Double)
        {
            amount = Convert.ToDecimal(field.Value.AsDouble());
            raw = field.Content ?? amount.ToString("N2");
            return amount > 0;
        }

        if (field.FieldType == DocumentFieldType.String)
        {
            var text = field.Value.AsString();
            raw = text;
            if (TryParseAmountFromWords(text, out var parsed))
            {
                amount = parsed;
                return true;
            }
        }

        return false;
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
    };

    private static readonly Dictionary<string, int> ScaleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hundred"] = 100, ["thousand"] = 1_000, ["million"] = 1_000_000
    };

    internal static bool TryParseAmountFromWords(string? source, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var text = source.ToLowerInvariant();
        var fracMatch = Regex.Match(text, @"(\d{1,2})\s*/\s*100");
        var cents = fracMatch.Success ? int.Parse(fracMatch.Groups[1].Value) : 0;

        text = Regex.Replace(text, @"[^a-z\s\-]", " ");
        text = Regex.Replace(text, @"\b(and|dollars?|only)\b", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(t => t.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        long total = 0;
        long current = 0;
        foreach (var token in tokens)
        {
            if (NumberWords.TryGetValue(token, out var n))
            {
                current += n;
                continue;
            }

            if (!ScaleWords.TryGetValue(token, out var scale))
                continue;

            if (scale == 100)
            {
                if (current == 0)
                    current = 1;
                current *= scale;
            }
            else
            {
                total += current * scale;
                current = 0;
            }
        }

        total += current;
        if (total <= 0 && cents <= 0)
            return false;
        amount = total + cents / 100m;
        return amount > 0m;
    }
}
