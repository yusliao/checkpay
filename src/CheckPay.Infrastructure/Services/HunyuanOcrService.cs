using System.Text.Json;
using System.Text.RegularExpressions;
using CheckPay.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TencentCloud.Common;
using TencentCloud.Hunyuan.V20230901;
using TencentCloud.Hunyuan.V20230901.Models;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 腾讯混元视觉模型 OCR 服务
/// 调用 hunyuan-vision 模型识别美国支票 / 银行扣款凭证
/// </summary>
public class HunyuanOcrService : IOcrService
{
    private readonly HunyuanClient _client;
    private readonly ILogger<HunyuanOcrService> _logger;
    private readonly IBlobStorageService _blobStorageService;
    private readonly string _model;

    /// <summary>
    /// 支票 OCR Prompt：Chain-of-Thought 引导模型分步定位 MICR 行、金额、日期
    /// 置信度直接输出 0.0-1.0 浮点数，精度远优于 high/medium/low 字符串
    /// </summary>
    private const string CheckOcrPrompt = """
        You are an expert US bank check reader. Follow these steps carefully and return ONLY a valid JSON object, no explanations, no markdown.

        Step 1 - Locate the MICR line (magnetic ink characters at the bottom of the check):
          Format: ⑆routing_number⑆ ⑈account_number⑈ check_number
          The check number is typically the 4-6 digit number at the far RIGHT of the MICR line.
          Also find the printed check number in the upper-right corner of the check.
          Cross-validate: if both MICR and printed numbers match, confidence is near 1.0.

        Step 2 - Locate the amount:
          Primary: the "courtesy amount box" (numeric amount in the top-right box, e.g. "$1,234.56")
          Validation: the "legal amount line" (written-out amount, e.g. "One thousand two hundred thirty-four and 56/100")
          If both agree, confidence is near 1.0.

        Step 3 - Locate the date:
          Common US formats: MM/DD/YYYY, Month DD YYYY, MM-DD-YY
          Always output in YYYY-MM-DD format.

        Step 4 - Output this exact JSON structure:
        {
          "check_number": "the check number as a string (digits only, no spaces)",
          "amount": 0.00,
          "date": "YYYY-MM-DD",
          "confidence": {
            "check_number": 0.0,
            "amount": 0.0,
            "date": 0.0
          }
        }

        Confidence scoring rules (output a decimal number between 0.0 and 1.0):
        - MICR and printed number match → check_number confidence: 0.95
        - Only one source found, clearly readable → 0.75
        - Partially visible or uncertain → 0.45
        - Cannot be found → set field to null, confidence: 0.1
        - Amount: both courtesy box and legal line agree → 0.95; only one source → 0.75; unclear → 0.45
        - Date: clearly printed, unambiguous → 0.90; partially legible → 0.50
        - amount must be a number without $ or comma symbols
        """;

    /// <summary>
    /// 银行扣款凭证 OCR Prompt：识别扣款金额、日期、银行流水号、支票号（可能不存在）
    /// 置信度直接输出 0.0-1.0 浮点数
    /// </summary>
    private const string DebitOcrPrompt = """
        You are a US bank debit document information extractor.
        CRITICAL: Your response must be ONLY a single valid JSON object. No explanations, no markdown, no text before or after the JSON. If you cannot read a field, set it to null.
        Extract from this bank debit, ACH transaction, or payment advice document:
        {
          "check_number": "the check number if clearly visible on the document, otherwise null",
          "amount": 0.00,
          "date": "YYYY-MM-DD",
          "bank_reference": "the bank reference number, trace number, or transaction ID",
          "confidence": {
            "check_number": 0.0,
            "amount": 0.0,
            "date": 0.0,
            "bank_reference": 0.0
          }
        }
        Confidence scoring rules (output a decimal number between 0.0 and 1.0):
        - Clearly visible and unambiguous → 0.90-0.95
        - Mostly legible, minor uncertainty → 0.70-0.80
        - Blurry, partially visible, or uncertain → 0.40-0.55
        - Cannot be found → set field to null, confidence: 0.1
        - amount must be a number without $ symbol
        - date must be in YYYY-MM-DD format
        - bank_reference is the transaction reference / trace number from the bank statement
        """;

    public HunyuanOcrService(
        IConfiguration configuration,
        ILogger<HunyuanOcrService> logger,
        IBlobStorageService blobStorageService)
    {
        var secretId = configuration["Hunyuan:SecretId"]
            ?? throw new InvalidOperationException("腾讯混元 SecretId 未配置");
        var secretKey = configuration["Hunyuan:SecretKey"]
            ?? throw new InvalidOperationException("腾讯混元 SecretKey 未配置");
        var region = configuration["Hunyuan:Region"] ?? "ap-guangzhou";

        _model = configuration["Ocr:Model"] ?? "hunyuan-vision";

        var credential = new Credential { SecretId = secretId, SecretKey = secretKey };
        _client = new HunyuanClient(credential, region);
        _logger = logger;
        _blobStorageService = blobStorageService;
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("混元 OCR 开始处理支票: {ImageUrl}", imageUrl);
        var dataUri = await BuildDataUriAsync(imageUrl, cancellationToken);
        var rawContent = await CallHunyuanVisionAsync(dataUri, CheckOcrPrompt);
        _logger.LogInformation("混元 OCR 原始响应（支票）: {Content}", rawContent);
        var result = ParseCheckOcrResponse(rawContent);
        _logger.LogInformation(
            "混元 OCR 解析结果 — 支票号: {CheckNumber} | 金额: {Amount} | 日期: {Date} | 置信度: 支票号={ConfCheckNumber:F2} 金额={ConfAmount:F2} 日期={ConfDate:F2}",
            result.CheckNumber,
            result.Amount,
            result.Date.ToString("yyyy-MM-dd"),
            result.ConfidenceScores.GetValueOrDefault("CheckNumber"),
            result.ConfidenceScores.GetValueOrDefault("Amount"),
            result.ConfidenceScores.GetValueOrDefault("Date"));
        return result;
    }

    public async Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("混元 OCR 开始处理扣款凭证: {ImageUrl}", imageUrl);
        var dataUri = await BuildDataUriAsync(imageUrl, cancellationToken);
        var rawContent = await CallHunyuanVisionAsync(dataUri, DebitOcrPrompt);
        _logger.LogInformation("混元 OCR 原始响应（扣款）: {Content}", rawContent);
        var result = ParseDebitOcrResponse(rawContent);
        _logger.LogInformation(
            "混元 OCR 解析结果 — 支票号: {CheckNumber} | 金额: {Amount} | 日期: {Date} | 流水号: {BankRef} | 置信度: 支票号={ConfCheckNumber:F2} 金额={ConfAmount:F2} 日期={ConfDate:F2} 流水号={ConfBankRef:F2}",
            result.CheckNumber ?? "null",
            result.Amount,
            result.Date?.ToString("yyyy-MM-dd") ?? "null",
            result.BankReference ?? "null",
            result.ConfidenceScores.GetValueOrDefault("CheckNumber"),
            result.ConfidenceScores.GetValueOrDefault("Amount"),
            result.ConfidenceScores.GetValueOrDefault("Date"),
            result.ConfidenceScores.GetValueOrDefault("BankReference"));
        return result;
    }

    // ── 公共辅助方法 ──────────────────────────────────────────────

    /// <summary>从 MinIO 下载图片并构建 Data URI（混元 API 无法访问内网 MinIO）</summary>
    private async Task<string> BuildDataUriAsync(string imageUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var imageStream = await _blobStorageService.DownloadAsync(imageUrl, cancellationToken);
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, cancellationToken);
            var imageBytes = memoryStream.ToArray();
            if (imageBytes.Length == 0)
                throw new InvalidOperationException($"MinIO返回空文件，url:{imageUrl}");

            _logger.LogInformation("图片下载成功，大小: {Size} bytes", imageBytes.Length);
            var base64Image = Convert.ToBase64String(imageBytes);
            var ext = Path.GetExtension(imageUrl).TrimStart('.').ToLowerInvariant();
            var mime = ext switch { "png" => "image/png", "gif" => "image/gif", "webp" => "image/webp", _ => "image/jpeg" };
            return $"data:{mime};base64,{base64Image}";
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"MinIO下载失败，url:{imageUrl}，原因:{ex.Message}", ex);
        }
    }

    private async Task<string> CallHunyuanVisionAsync(string dataUri, string prompt)
    {
        var req = new ChatCompletionsRequest
        {
            Model = _model,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Contents =
                    [
                        new Content { Type = "image_url", ImageUrl = new ImageUrl { Url = dataUri } },
                        new Content { Type = "text", Text = prompt }
                    ]
                }
            ]
        };

        var resp = await _client.ChatCompletions(req);
        return resp.Choices[0].Message.Content
            ?? throw new InvalidOperationException("混元返回空响应");
    }

    // ── 解析方法 ──────────────────────────────────────────────────

    private static OcrResultDto ParseCheckOcrResponse(string rawContent)
    {
        var json = CleanJson(rawContent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var checkNumber = GetString(root, "check_number") ?? string.Empty;
        var amount = GetDecimal(root, "amount") ?? 0m;
        var date = GetDate(root, "date") ?? DateTime.UtcNow;

        var confidenceScores = new Dictionary<string, double>
        {
            { "CheckNumber", 0.5 }, { "Amount", 0.5 }, { "Date", 0.5 }
        };
        if (root.TryGetProperty("confidence", out var conf))
        {
            confidenceScores["CheckNumber"] = GetDoubleConfidence(conf, "check_number");
            confidenceScores["Amount"]      = GetDoubleConfidence(conf, "amount");
            confidenceScores["Date"]        = GetDoubleConfidence(conf, "date");
        }

        return new OcrResultDto(checkNumber, amount, date, confidenceScores);
    }

    private static DebitOcrResultDto ParseDebitOcrResponse(string rawContent)
    {
        var json = CleanJson(rawContent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var checkNumber   = GetString(root, "check_number");
        var amount        = GetDecimal(root, "amount");
        var date          = GetDate(root, "date");
        var bankReference = GetString(root, "bank_reference");

        var confidenceScores = new Dictionary<string, double>
        {
            { "CheckNumber", 0.5 }, { "Amount", 0.5 }, { "Date", 0.5 }, { "BankReference", 0.5 }
        };
        if (root.TryGetProperty("confidence", out var conf))
        {
            confidenceScores["CheckNumber"]   = GetDoubleConfidence(conf, "check_number");
            confidenceScores["Amount"]        = GetDoubleConfidence(conf, "amount");
            confidenceScores["Date"]          = GetDoubleConfidence(conf, "date");
            confidenceScores["BankReference"] = GetDoubleConfidence(conf, "bank_reference");
        }

        return new DebitOcrResultDto(checkNumber, amount, date, bankReference, confidenceScores);
    }

    // ── 工具方法 ──────────────────────────────────────────────────

    /// <summary>
    /// 从模型响应中提取第一个完整 JSON 对象。
    /// 兼容三种情况：纯 JSON、单个 ```json``` 代码块、被解释性文字包裹的 JSON（模型不服管教时）
    /// </summary>
    private static string CleanJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new JsonException("OCR 响应为空");

        // 1. 尝试直接剥掉首尾 markdown 代码围栏（最常见的正常情况）
        var stripped = Regex.Replace(raw.Trim(), @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline).Trim();
        if (stripped.StartsWith('{') && stripped.EndsWith('}'))
            return stripped;

        // 2. 模型返回了解释文字 + 多个 JSON 块 → 提取第一个完整 {} 对象
        var start = raw.IndexOf('{');
        if (start < 0)
            throw new JsonException($"响应中未找到 JSON 对象，原始内容: {raw[..Math.Min(200, raw.Length)]}");

        var depth = 0;
        for (var i = start; i < raw.Length; i++)
        {
            if (raw[i] == '{') depth++;
            else if (raw[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return raw.Substring(start, i - start + 1);
            }
        }

        throw new JsonException($"响应中的 JSON 对象括号不匹配，原始内容: {raw[..Math.Min(200, raw.Length)]}");
    }

    private static string? GetString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static decimal? GetDecimal(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : decimal.TryParse(el.GetString(), out var v) ? v : null;
    }

    private static DateTime? GetDate(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return DateTime.TryParse(el.GetString(), out var v) ? v : null;
    }

    /// <summary>
    /// 解析置信度：优先读数字（新 Prompt），兼容旧字符串格式（high/medium/low）
    /// </summary>
    private static double GetDoubleConfidence(JsonElement conf, string key)
    {
        if (!conf.TryGetProperty(key, out var val) || val.ValueKind == JsonValueKind.Null)
            return 0.5;

        if (val.ValueKind == JsonValueKind.Number)
        {
            var v = val.GetDouble();
            return Math.Clamp(v, 0.0, 1.0);
        }

        // 兼容旧 Prompt 返回的字符串格式，以及模型偶尔返回的范围字符串如 "0.40-0.55"
        var str = val.GetString()?.ToLowerInvariant() ?? "";
        if (str.Contains('-') && str.Length > 1)
        {
            // 取范围均值，如 "0.40-0.55" → 0.475
            var parts = str.Split('-');
            if (parts.Length == 2
                && double.TryParse(parts[0].Trim(), out var lo)
                && double.TryParse(parts[1].Trim(), out var hi))
                return Math.Clamp((lo + hi) / 2.0, 0.0, 1.0);
        }
        return str switch
        {
            "high"   => 0.92,
            "medium" => 0.72,
            "low"    => 0.42,
            _        => 0.5
        };
    }
}
