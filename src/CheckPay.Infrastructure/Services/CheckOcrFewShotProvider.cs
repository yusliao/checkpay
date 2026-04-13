using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 从 <see cref="OcrTrainingSample"/>（document_type=check）读取最近样本，拼成混元 few-shot 纠偏段。
/// </summary>
public sealed class CheckOcrFewShotProvider(
    IApplicationDbContext dbContext,
    IConfiguration configuration,
    ILogger<CheckOcrFewShotProvider> logger) : ICheckOcrFewShotProvider
{
    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const int MicrTruncateLen = 160;

    public async Task<string> BuildCheckPromptAugmentationAsync(CancellationToken cancellationToken = default)
    {
        var maxSamples = 5;
        if (int.TryParse(configuration["Ocr:CheckFewShotMaxSamples"], out var ms))
            maxSamples = ms;
        if (maxSamples <= 0)
            return string.Empty;

        var maxChars = 12_000;
        if (int.TryParse(configuration["Ocr:CheckFewShotMaxChars"], out var mc))
            maxChars = mc;

        var ordered = await CheckOcrTrainingSamplePool.LoadOrderedAsync(
            dbContext, CheckOcrTrainingSamplePool.DefaultPoolTake, maxSamples, cancellationToken);

        if (ordered.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Prior audited check OCR examples (same JSON field names as your output)");
        sb.AppendLine("These describe past cases: \"model_extracted\" is what the pipeline captured at training time; \"audited_ground_truth\" is what finance confirmed.");
        sb.AppendLine("They are NOT the current image. Do not copy unrelated values. Use them to avoid repeating mistakes (e.g. MICR vs printed check number, 9-digit RTN, field order).");
        sb.AppendLine();

        var used = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var block = FormatSampleBlock(i + 1, ordered[i]);
            if (sb.Length + block.Length > maxChars)
            {
                logger.LogWarning(
                    "支票 OCR few-shot 已达长度上限 {MaxChars}，已使用 {Used}/{Total} 条",
                    maxChars, used, ordered.Count);
                break;
            }

            sb.Append(block);
            used++;
        }

        if (used == 0)
            return string.Empty;

        logger.LogInformation("支票 OCR few-shot 已注入 {Count} 条训练样本，约 {Len} 字符", used, sb.Length);
        return sb.ToString();
    }

    private string FormatSampleBlock(int index, OcrTrainingSample s)
    {
        var modelSide = BuildFieldDictionary(s, useCorrect: false);
        var truthSide = BuildFieldDictionary(s, useCorrect: true);

        var sb = new StringBuilder();
        sb.AppendLine($"### Example {index} (saved {s.CreatedAt:yyyy-MM-dd} UTC)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new { model_extracted = modelSide, audited_ground_truth = truthSide }, JsonWrite));
        sb.AppendLine("```");
        if (!string.IsNullOrWhiteSpace(s.Notes))
        {
            sb.AppendLine($"Notes: {TruncateSingleLine(s.Notes!, 400)}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static Dictionary<string, object?> BuildFieldDictionary(OcrTrainingSample s, bool useCorrect)
    {
        string? cn = useCorrect ? s.CorrectCheckNumber : s.OcrCheckNumber;
        decimal? amt = useCorrect ? s.CorrectAmount : s.OcrAmount;
        DateTime? dt = useCorrect ? s.CorrectDate : s.OcrDate;

        var achJson = useCorrect ? s.CorrectAchExtensionJson : s.OcrAchExtensionJson;
        var ach = CheckAchExtensionData.Deserialize(achJson);

        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["check_number"] = CheckOcrTrainingSampleDiff.Norm(cn),
            ["amount"] = amt,
            ["date"] = dt?.ToUniversalTime().ToString("yyyy-MM-dd")
        };

        if (ach == null)
            return d;

        void put(string key, string? val, bool truncate = false)
        {
            var v = CheckOcrTrainingSampleDiff.Norm(val);
            if (v == null) return;
            if (truncate && v.Length > MicrTruncateLen)
                v = v[..MicrTruncateLen] + "…";
            d[key] = v;
        }

        put("routing_number", ach.RoutingNumber);
        put("account_number", ach.AccountNumber);
        put("bank_name", ach.BankName);
        put("account_holder_name", ach.AccountHolderName);
        put("account_address", ach.AccountAddress);
        put("account_type", ach.AccountType);
        put("pay_to_order_of", ach.PayToOrderOf);
        put("for_memo", ach.ForMemo);
        put("micr_line_raw", ach.MicrLineRaw, truncate: true);
        put("check_number_micr", ach.CheckNumberMicr);
        put("micr_field_order_note", ach.MicrFieldOrderNote);

        return d;
    }

    private static string TruncateSingleLine(string s, int max)
    {
        var one = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
