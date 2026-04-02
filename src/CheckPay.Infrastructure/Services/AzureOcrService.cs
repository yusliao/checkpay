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

    public AzureOcrService(
        IConfiguration configuration,
        ILogger<AzureOcrService> logger,
        IBlobStorageService blobStorageService)
    {
        var endpoint = configuration["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("Azure AI Vision Endpoint 未配置");
        var apiKey = configuration["Azure:DocumentIntelligence:ApiKey"]
            ?? throw new InvalidOperationException("Azure AI Vision ApiKey 未配置");

        _client = new ImageAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _logger = logger;
        _blobStorageService = blobStorageService;
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Azure Vision OCR 开始处理支票: {ImageUrl}", imageUrl);

        // MinIO 内网 URL 无法被 Azure API 访问，先下载图片流
        using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);

        // BinaryData.FromStream 会读取当前位置到结尾，先复制到 MemoryStream 并重置位置
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;
        _logger.LogInformation("Azure Vision 图片下载完成，大小: {Size} bytes", memoryStream.Length);

        var analysisResult = await _client.AnalyzeAsync(
            BinaryData.FromStream(memoryStream),
            VisualFeatures.Read,
            cancellationToken: cancellationToken);

        var rawText = ExtractRawText(analysisResult.Value);
        _logger.LogInformation("Azure Vision OCR 提取原始文字: {RawText}", rawText);

        var (checkNumber, checkConf)  = ParseCheckNumber(rawText);
        var (amount, amountConf)      = ParseAmount(rawText);
        var (date, dateConf)          = ParseDate(rawText);

        _logger.LogInformation(
            "Azure Vision OCR 解析结果 — 支票号: {CheckNumber}({ConfCN:F2}) | 金额: {Amount}({ConfAmt:F2}) | 日期: {Date}({ConfDate:F2})",
            checkNumber, checkConf, amount, amountConf, date?.ToString("yyyy-MM-dd"), dateConf);

        return new OcrResultDto(
            CheckNumber: checkNumber,
            Amount: amount,
            Date: date ?? DateTime.UtcNow,
            ConfidenceScores: new Dictionary<string, double>
            {
                { "CheckNumber", checkConf },
                { "Amount",      amountConf },
                { "Date",        dateConf }
            });
    }

    /// <summary>
    /// Azure AI Vision 不识别扣款凭证结构，返回空结果由财务手动填写
    /// </summary>
    public Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(new DebitOcrResultDto(null, null, null, null, new Dictionary<string, double>()));

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
}
