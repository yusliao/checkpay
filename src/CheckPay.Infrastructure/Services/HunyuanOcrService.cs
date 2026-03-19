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

    private const string CheckOcrPrompt = """
        You are a US bank check information extractor.
        Extract the following fields from this check image and return ONLY a valid JSON object, no explanations, no markdown:
        {
          "check_number": "the check number printed on the check (MICR line or upper right)",
          "amount": 0.00,
          "date": "YYYY-MM-DD",
          "confidence": {
            "check_number": "high|medium|low",
            "amount": "high|medium|low",
            "date": "high|medium|low"
          }
        }
        Rules:
        - amount must be a number without $ symbol
        - date must be in YYYY-MM-DD format
        - Set confidence to "low" if the field is blurry, partially visible, or uncertain
        - If a field cannot be found, set it to null
        """;

    /// <summary>
    /// 银行扣款凭证 OCR Prompt：识别扣款金额、日期、银行流水号、支票号（可能不存在）
    /// </summary>
    private const string DebitOcrPrompt = """
        You are a US bank debit document information extractor.
        Extract the following fields from this bank debit, ACH transaction, or payment advice document
        and return ONLY a valid JSON object, no explanations, no markdown:
        {
          "check_number": "the check number if clearly visible on the document, otherwise null",
          "amount": 0.00,
          "date": "YYYY-MM-DD",
          "bank_reference": "the bank reference number, trace number, or transaction ID",
          "confidence": {
            "check_number": "high|medium|low",
            "amount": "high|medium|low",
            "date": "high|medium|low",
            "bank_reference": "high|medium|low"
          }
        }
        Rules:
        - amount must be a number without $ symbol
        - date must be in YYYY-MM-DD format
        - bank_reference is the transaction reference / trace number from the bank statement
        - Set confidence to "low" if the field is blurry, partially visible, or uncertain
        - If a field cannot be found, set it to null
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
        _logger.LogDebug("混元 OCR 原始响应（支票）: {Content}", rawContent);
        return ParseCheckOcrResponse(rawContent);
    }

    public async Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("混元 OCR 开始处理扣款凭证: {ImageUrl}", imageUrl);
        var dataUri = await BuildDataUriAsync(imageUrl, cancellationToken);
        var rawContent = await CallHunyuanVisionAsync(dataUri, DebitOcrPrompt);
        _logger.LogDebug("混元 OCR 原始响应（扣款）: {Content}", rawContent);
        return ParseDebitOcrResponse(rawContent);
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
            Model = "hunyuan-vision",
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
            confidenceScores["CheckNumber"] = MapConfidenceLevel(conf, "check_number");
            confidenceScores["Amount"]      = MapConfidenceLevel(conf, "amount");
            confidenceScores["Date"]        = MapConfidenceLevel(conf, "date");
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
            confidenceScores["CheckNumber"]   = MapConfidenceLevel(conf, "check_number");
            confidenceScores["Amount"]        = MapConfidenceLevel(conf, "amount");
            confidenceScores["Date"]          = MapConfidenceLevel(conf, "date");
            confidenceScores["BankReference"] = MapConfidenceLevel(conf, "bank_reference");
        }

        return new DebitOcrResultDto(checkNumber, amount, date, bankReference, confidenceScores);
    }

    // ── 工具方法 ──────────────────────────────────────────────────

    private static string CleanJson(string raw) =>
        Regex.Replace(raw.Trim(), @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline).Trim();

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

    private static double MapConfidenceLevel(JsonElement conf, string key)
    {
        if (!conf.TryGetProperty(key, out var val) || val.ValueKind == JsonValueKind.Null)
            return 0.5;

        return val.GetString()?.ToLowerInvariant() switch
        {
            "high"   => 0.92,
            "medium" => 0.72,
            "low"    => 0.42,
            _        => 0.5
        };
    }
}
