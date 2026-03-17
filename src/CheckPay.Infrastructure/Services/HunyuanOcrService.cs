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
/// 调用 hunyuan-vision 模型识别美国支票中的支票号、金额、日期
/// </summary>
public class HunyuanOcrService : IOcrService
{
    private readonly HunyuanClient _client;
    private readonly ILogger<HunyuanOcrService> _logger;

    // 用 Prompt 让模型只返回 JSON，省得解析废话
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

    public HunyuanOcrService(IConfiguration configuration, ILogger<HunyuanOcrService> logger)
    {
        var secretId = configuration["Hunyuan:SecretId"]
            ?? throw new InvalidOperationException("腾讯混元 SecretId 未配置");
        var secretKey = configuration["Hunyuan:SecretKey"]
            ?? throw new InvalidOperationException("腾讯混元 SecretKey 未配置");
        var region = configuration["Hunyuan:Region"] ?? "ap-guangzhou";

        var credential = new Credential { SecretId = secretId, SecretKey = secretKey };
        _client = new HunyuanClient(credential, region);
        _logger = logger;
    }

    public async Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("混元 OCR 开始处理: {ImageUrl}", imageUrl);

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
                        new Content
                        {
                            Type = "image_url",
                            ImageUrl = new ImageUrl { Url = imageUrl }
                        },
                        new Content
                        {
                            Type = "text",
                            Text = CheckOcrPrompt
                        }
                    ]
                }
            ]
        };

        var resp = await _client.ChatCompletions(req);

        var rawContent = resp.Choices[0].Message.Content
            ?? throw new InvalidOperationException("混元返回空响应，这憨批 API 出什么鬼问题了");

        _logger.LogDebug("混元 OCR 原始响应: {Content}", rawContent);

        return ParseOcrResponse(rawContent);
    }

    private static OcrResultDto ParseOcrResponse(string rawContent)
    {
        // 清理模型可能包的 markdown 代码块
        var json = Regex.Replace(rawContent.Trim(), @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline).Trim();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var checkNumber = root.TryGetProperty("check_number", out var cn) && cn.ValueKind != JsonValueKind.Null
            ? cn.GetString() ?? string.Empty
            : string.Empty;

        var amount = 0m;
        if (root.TryGetProperty("amount", out var amt) && amt.ValueKind != JsonValueKind.Null)
        {
            amount = amt.ValueKind == JsonValueKind.Number
                ? amt.GetDecimal()
                : decimal.TryParse(amt.GetString(), out var parsedAmt) ? parsedAmt : 0m;
        }

        var date = DateTime.UtcNow;
        if (root.TryGetProperty("date", out var dt) && dt.ValueKind != JsonValueKind.Null)
        {
            if (DateTime.TryParse(dt.GetString(), out var parsedDate))
                date = parsedDate;
        }

        var confidenceScores = new Dictionary<string, double>
        {
            { "CheckNumber", 0.5 },
            { "Amount", 0.5 },
            { "Date", 0.5 }
        };

        if (root.TryGetProperty("confidence", out var conf))
        {
            confidenceScores["CheckNumber"] = MapConfidenceLevel(conf, "check_number");
            confidenceScores["Amount"] = MapConfidenceLevel(conf, "amount");
            confidenceScores["Date"] = MapConfidenceLevel(conf, "date");
        }

        return new OcrResultDto(checkNumber, amount, date, confidenceScores);
    }

    /// <summary>
    /// 将模型返回的 high/medium/low 映射到系统置信度分数
    /// high ≥0.85（绿） / medium 0.60-0.85（橙） / low &lt;0.60（红）
    /// </summary>
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
