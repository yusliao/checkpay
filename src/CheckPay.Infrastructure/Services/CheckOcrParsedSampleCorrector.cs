using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// Azure 等基于规则的引擎无法注入 few-shot；当当前解析结果与某条训练样本的「OCR 侧」一致时，用该样本的核定值覆盖对应字段。
/// </summary>
public sealed class CheckOcrParsedSampleCorrector(
    IApplicationDbContext dbContext,
    IConfiguration configuration,
    ILogger<CheckOcrParsedSampleCorrector> logger) : ICheckOcrParsedSampleCorrector
{
    public async Task<OcrResultDto> ApplyIfMatchedAsync(OcrResultDto parsed, CancellationToken cancellationToken = default)
    {
        var enabled = true;
        if (bool.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionEnabled"], out var en))
            enabled = en;
        if (!enabled)
            return parsed;

        var maxScan = 40;
        if (int.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionMaxScan"], out var mx))
            maxScan = mx;
        if (maxScan <= 0)
            return parsed;

        var poolTake = CheckOcrTrainingSamplePool.DefaultPoolTake;
        if (int.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionPoolTake"], out var pt) && pt > 0)
            poolTake = pt;

        var ordered = await CheckOcrTrainingSamplePool.LoadOrderedAsync(
            dbContext, poolTake, maxScan, cancellationToken);

        foreach (var s in ordered)
        {
            if (!CheckOcrTrainingSampleDiff.HasStructuredDiff(s))
                continue;
            if (!IsStrongMatch(parsed, s))
                continue;

            var merged = MergeFromSample(parsed, s);
            logger.LogInformation(
                "Azure/规则支票 OCR 已应用训练样本纠偏 SampleId={SampleId} CreatedAt={CreatedAt:O}",
                s.Id,
                s.CreatedAt);
            return merged;
        }

        return parsed;
    }

    /// <summary>
    /// 强匹配：路由号（若有）+ 训练时 OCR 支票号与当前解析一致；或无路有号时 支票号+金额+日期 与样本 OCR 侧一致。
    /// </summary>
    private static bool IsStrongMatch(OcrResultDto current, OcrTrainingSample s)
    {
        var oAch = CheckAchExtensionData.Deserialize(s.OcrAchExtensionJson);
        var oRtn = CheckOcrTrainingSampleDiff.NormDigits(oAch?.RoutingNumber);
        var cRtn = CheckOcrTrainingSampleDiff.NormDigits(current.RoutingNumber);

        var oCrCn = CheckOcrTrainingSampleDiff.Norm(s.OcrCheckNumber);
        var curCn = CheckOcrTrainingSampleDiff.Norm(current.CheckNumber);

        if (oCrCn == null || curCn == null)
            return false;

        if (!string.Equals(oCrCn, curCn, StringComparison.OrdinalIgnoreCase))
            return false;

        if (oRtn != null && cRtn != null)
            return string.Equals(oRtn, cRtn, StringComparison.Ordinal);

        // 双方都没有9 位路由时，用金额+日期再收紧，避免不同支票同号
        if (oRtn != null || cRtn != null)
            return false;

        if (!s.OcrAmount.HasValue || s.OcrAmount.Value != current.Amount)
            return false;
        if (!s.OcrDate.HasValue || s.OcrDate.Value.Date != current.Date.Date)
            return false;

        return true;
    }

    private static OcrResultDto MergeFromSample(OcrResultDto current, OcrTrainingSample s)
    {
        var cAch = CheckAchExtensionData.Deserialize(s.CorrectAchExtensionJson);

        var checkNumber = !string.IsNullOrWhiteSpace(s.CorrectCheckNumber)
            ? s.CorrectCheckNumber.Trim()
            : current.CheckNumber;

        var amount = s.CorrectAmount ?? current.Amount;

        var date = s.CorrectDate ?? current.Date;
        if (date.Kind == DateTimeKind.Unspecified)
            date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
        else
            date = date.ToUniversalTime();

        string? pick(string? correct, string? fallback) =>
            !string.IsNullOrWhiteSpace(correct) ? correct.Trim() : fallback;

        var routing = pick(cAch?.RoutingNumber, current.RoutingNumber);
        if (routing is { Length: not 9 } or null)
            routing = current.RoutingNumber;
        else if (!routing.All(char.IsDigit))
            routing = current.RoutingNumber;

        var merged = new OcrResultDto(
            CheckNumber: checkNumber,
            Amount: amount,
            Date: date,
            ConfidenceScores: BoostConfidenceForTrainingMerge(current.ConfidenceScores),
            RoutingNumber: routing,
            AccountNumber: pick(cAch?.AccountNumber, current.AccountNumber),
            BankName: pick(cAch?.BankName, current.BankName),
            AccountHolderName: pick(cAch?.AccountHolderName, current.AccountHolderName),
            AccountAddress: pick(cAch?.AccountAddress, current.AccountAddress),
            AccountType: pick(cAch?.AccountType, current.AccountType),
            PayToOrderOf: pick(cAch?.PayToOrderOf, current.PayToOrderOf),
            ForMemo: pick(cAch?.ForMemo, current.ForMemo),
            MicrLineRaw: pick(cAch?.MicrLineRaw, current.MicrLineRaw),
            CheckNumberMicr: pick(cAch?.CheckNumberMicr, current.CheckNumberMicr),
            MicrFieldOrderNote: pick(cAch?.MicrFieldOrderNote, current.MicrFieldOrderNote),
            ExtractedText: current.ExtractedText);

        return merged;
    }

    private static Dictionary<string, double> BoostConfidenceForTrainingMerge(
        Dictionary<string, double> original)
    {
        var d = new Dictionary<string, double>(original, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
 {
                     "CheckNumber", "CheckNumberMicr", "Amount", "Date", "RoutingNumber", "AccountNumber",
                     "BankName", "AccountHolderName", "AccountAddress", "AccountType", "PayToOrderOf", "ForMemo",
                     "MicrLineRaw"
                 })
        {
            if (d.ContainsKey(key))
                d[key] = Math.Clamp(d[key] + 0.12, 0.0, 0.95);
        }

        return d;
    }
}
