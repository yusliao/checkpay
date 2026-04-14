using System.Text.RegularExpressions;
using Azure;
using Azure.AI.Vision.ImageAnalysis;
using CheckPay.Application.Common.Interfaces;
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
    private readonly IBlobStorageService _blobStorageService;
    private readonly ICheckOcrParsedSampleCorrector _parsedSampleCorrector;
    private readonly ILogger<AzureOcrService> _logger;

    // MICR 行格式：⑆路由号⑆ ⑈账号⑈ 支票号（或简化版数字序列）
    // 美国支票 MICR 字符集：⑆ = 路由符，⑈ = 账号符，⑉ = 金额符
    // 退而求其次：匹配底部的短数字序列（4-6位）作为支票号候选
    private static readonly Regex MicrCheckNumberRegex = new(
        @"(?:⑆[0-9⑆⑈ ]+⑈[0-9⑆⑈ ]+\s+|[⑆⑈])([0-9]{4,6})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // 右上角印刷支票号（通常 4-6 位，与 MICR 互相验证）
    private static readonly Regex PrintedCheckNumberRegex = new(
        @"(?:check\s*(?:no\.?|number|#)\s*:?\s*|^|\s)(\d{4,6})(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // 金额：$1,234.56 或 1234.56 格式
    private static readonly Regex AmountRegex = new(
        @"\$\s*([\d,]+\.\d{2})\b|([\d,]{1,10}\.\d{2})\b",
        RegexOptions.Compiled);

    // 日期：MM/DD/YYYY、MM-DD-YYYY、Month DD YYYY、MM/DD/YY
    private static readonly Regex DateRegex = new(
        @"\b(?:(?:0?[1-9]|1[0-2])[/\-](?:0?[1-9]|[12]\d|3[01])[/\-](?:\d{4}|\d{2})|" +
        @"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 扣款凭证：流水号 / Trace / Reference 等标签后的 token
    private static readonly Regex BankReferenceLabelRegex = new(
        @"(?i)(?:ref(?:erence)?|trace(?:\s*(?:no\.?|number|#))?|confirmation|confirm(?:ation)?\s*#|trans(?:action)?(?:\s*id|#)?)\s*[:\#]?\s*([A-Z0-9][A-Z0-9\-]{3,48})",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PayToOrderLineRegex = new(
        @"(?i)pay\s+to\s+the\s+order\s+of\s*[:\s]*([^\r\n]{2,200})",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public AzureOcrService(
        IConfiguration configuration,
        ILogger<AzureOcrService> logger,
        IBlobStorageService blobStorageService,
        ICheckOcrParsedSampleCorrector parsedSampleCorrector)
    {
        var endpoint = configuration["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("Azure AI Vision Endpoint 未配置");
        var apiKey = configuration["Azure:DocumentIntelligence:ApiKey"]
            ?? throw new InvalidOperationException("Azure AI Vision ApiKey 未配置");

        _client = new ImageAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _logger = logger;
        _blobStorageService = blobStorageService;
        _parsedSampleCorrector = parsedSampleCorrector;
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理支票: {ImageUrl}", imageUrl);

        var rawText = await GetReadTextFromImageAsync(imageUrl, cancellationToken);
        _logger.LogInformation("Azure Vision OCR 提取原始文字: {RawText}", rawText);

        var (checkNumber, checkConf)  = ParseCheckNumber(rawText);
        var (amount, amountConf)      = ParseAmount(rawText);
        var (date, dateConf)          = ParseDate(rawText);

        _logger.LogInformation(
            "Azure Vision OCR 解析结果 — 支票号: {CheckNumber}({ConfCN:F2}) | 金额: {Amount}({ConfAmt:F2}) | 日期: {Date}({ConfDate:F2})",
            checkNumber, checkConf, amount, amountConf, date?.ToString("yyyy-MM-dd"), dateConf);

        var (routing, acct, micrLine, rtConf, acConf, micConf) = ParseMicrHeuristic(rawText);
        var (payToLine, payToConf) = ParsePayToOrderLine(rawText);

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

    private async Task<string> GetReadTextFromImageAsync(string imageUrl, CancellationToken cancellationToken)
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

        return ExtractRawText(analysisResult.Value);
    }

    /// <summary>从 OCR 全文尾部启发式提取 MICR：9 位路由 + 较长账号串（置信度保守）。</summary>
    private static (string? routing, string? account, string? micrLine, double rtConf, double acConf, double micConf)
        ParseMicrHeuristic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null, null, 0.1, 0.1, 0.1);

        var tailStart = Math.Max(0, text.Length - 400);
        var tail = text[tailStart..];
        var digitsRuns = System.Text.RegularExpressions.Regex.Matches(tail, @"\d{4,}")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Value)
            .ToList();

        string? routing = null;
        string? account = null;
        foreach (var run in digitsRuns.Where(r => r.Length == 9))
        {
            routing = run;
            break;
        }

        foreach (var run in digitsRuns.OrderByDescending(r => r.Length))
        {
            if (run == routing) continue;
            if (run.Length >= 8)
            {
                account = run;
                break;
            }
        }

        var micrLine = string.Join(" ", digitsRuns.TakeLast(5));
        var rtConf = routing != null ? 0.42 : 0.1;
        var acConf = account != null ? 0.38 : 0.1;
        var micConf = micrLine.Length > 0 ? 0.35 : 0.1;
        return (routing, account, micrLine.Length > 0 ? micrLine : null, rtConf, acConf, micConf);
    }

    /// <summary>
    /// 扣款凭证：使用 Read API 抽全文 + 与支票类似的启发式字段解析（训练标注与管理端预览）。
    /// </summary>
    public async Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理扣款凭证: {ImageUrl}", imageUrl);
        var rawText = await GetReadTextFromImageAsync(imageUrl, cancellationToken);
        _logger.LogInformation("Azure Vision OCR 扣款凭证全文: {RawText}", rawText);

        var (checkStr, checkConf) = ParseCheckNumber(rawText);
        var checkNumber = string.IsNullOrWhiteSpace(checkStr) ? null : checkStr.Trim();

        var (amountVal, amountConf) = ParseAmount(rawText);
        decimal? amount = amountVal > 0m ? amountVal : null;
        if (amount is null) amountConf = 0.12;

        var (dateVal, dateConf) = ParseDate(rawText);

        var (bankRef, refConf) = ParseBankReference(rawText);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheckNumber"] = checkNumber is null ? 0.12 : checkConf,
            ["Amount"] = amount is null ? 0.12 : amountConf,
            ["Date"] = dateVal is null ? 0.12 : dateConf,
            ["BankReference"] = bankRef is null ? 0.12 : refConf
        };

        return new DebitOcrResultDto(checkNumber, amount, dateVal, bankRef, scores, RawExtractedText: rawText);
    }

    // ── 文字提取 ──────────────────────────────────────────────────

    private static string ExtractRawText(ImageAnalysisResult result)
    {
        if (result.Read?.Blocks == null) return string.Empty;
        return string.Join("\n", result.Read.Blocks
            .SelectMany(b => b.Lines)
            .Select(l => l.Text));
    }

    // ── 字段解析 ──────────────────────────────────────────────────

    /// <summary>
    /// 支票号解析策略：
    /// 1. 优先匹配 MICR 行（底部磁性墨水字符）
    /// 2. 次选右上角印刷支票号
    /// 两者一致 → 高置信度 0.92，只找到一个 → 0.72，找不到 → 0.1
    /// </summary>
    private static (string checkNumber, double confidence) ParseCheckNumber(string text)
    {
        var micrMatch    = MicrCheckNumberRegex.Match(text);
        var printedMatch = PrintedCheckNumberRegex.Match(text);

        var micrNumber    = micrMatch.Success    ? micrMatch.Groups[1].Value.Trim()    : null;
        var printedNumber = printedMatch.Success ? printedMatch.Groups[1].Value.Trim() : null;

        if (micrNumber != null && printedNumber != null)
        {
            // 两者一致 → 高置信度
            if (micrNumber == printedNumber)
                return (micrNumber, 0.92);
            // 两者不一致 → 优先 MICR，中等置信度
            return (micrNumber, 0.55);
        }

        if (micrNumber != null)    return (micrNumber,    0.72);
        if (printedNumber != null) return (printedNumber, 0.62);
        return (string.Empty, 0.1);
    }

    /// <summary>
    /// 金额解析：找最大金额（通常是支票金额），过滤掉印章/路由等小数字
    /// </summary>
    private static (decimal amount, double confidence) ParseAmount(string text)
    {
        var matches = AmountRegex.Matches(text);
        if (!matches.Any()) return (0m, 0.1);

        var amounts = matches
            .Select(m =>
            {
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .Replace(",", "");
                return decimal.TryParse(raw, out var v) ? v : 0m;
            })
            .Where(v => v > 0)
            .OrderByDescending(v => v)
            .ToList();

        if (!amounts.Any()) return (0m, 0.1);

        // 找到最大值，置信度取决于候选数量（太多说明识别混乱）
        var best = amounts[0];
        var conf = amounts.Count == 1 ? 0.88 : amounts.Count <= 3 ? 0.72 : 0.50;
        return (best, conf);
    }

    /// <summary>日期解析：取第一个匹配的日期格式</summary>
    private static (DateTime? date, double confidence) ParseDate(string text)
    {
        var match = DateRegex.Match(text);
        if (!match.Success) return (null, 0.1);

        if (DateTime.TryParse(match.Value, out var dt))
            return (dt, 0.82);

        return (null, 0.1);
    }

    /// <summary>从支票全文启发式提取 Pay to the order of 行，作为公司名称与 Pay to 字段来源。</summary>
    private static (string? line, double confidence) ParsePayToOrderLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0.1);

        var m = PayToOrderLineRegex.Match(text);
        if (!m.Success)
            return (null, 0.1);

        var raw = m.Groups[1].Value.Trim();
        if (raw.Length < 2)
            return (null, 0.1);

        var cut = Regex.Replace(raw, @"\s{2,}", " ");
        return (cut, 0.48);
    }

    /// <summary>从银行扣款/ACH 回单文字中启发式提取流水号或 Trace。</summary>
    private static (string? reference, double confidence) ParseBankReference(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0.1);

        var m = BankReferenceLabelRegex.Match(text);
        if (m.Success)
            return (m.Groups[1].Value.Trim(), 0.78);

        var longTokens = Regex.Matches(text, @"\b[A-Z0-9]{14,}\b");
        if (longTokens.Count > 0)
        {
            var best = longTokens.Cast<Match>().OrderByDescending(x => x.Length).First().Value;
            return (best, 0.48);
        }

        var digitRuns = Regex.Matches(text, @"\d{12,22}\b");
        if (digitRuns.Count > 0)
            return (digitRuns[digitRuns.Count - 1].Value, 0.42);

        return (null, 0.1);
    }
}
