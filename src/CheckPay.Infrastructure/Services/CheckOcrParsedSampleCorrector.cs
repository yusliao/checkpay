using System.Globalization;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// Azure 等基于规则的引擎无法注入 few-shot；当当前解析结果与训练样本匹配时，用样本核定值纠偏（强匹配或相似度 + 字段级门控）。
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
                "Azure/规则支票 OCR 已应用训练样本纠偏(强匹配) SampleId={SampleId} CreatedAt={CreatedAt:O}",
                s.Id,
                s.CreatedAt);
            return merged;
        }

        var mode = configuration["Ocr:CheckAzureTrainingCorrectionMode"]?.Trim() ?? "StrongOnly";
        if (!string.Equals(mode, "Similarity", StringComparison.OrdinalIgnoreCase))
            return parsed;

        var minSim = 0.80;
        _ = double.TryParse(
            configuration["Ocr:CheckAzureTrainingCorrectionMinSimilarity"],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out minSim);

        var maxFieldConf = 0.62;
        _ = double.TryParse(
            configuration["Ocr:CheckAzureTrainingCorrectionSimilarityMaxFieldConfidence"],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out maxFieldConf);

        OcrTrainingSample? best = null;
        var bestScore = 0.0;
        foreach (var s in ordered)
        {
            if (!CheckOcrTrainingSampleDiff.HasStructuredDiff(s))
                continue;

            var curText = parsed.ExtractedText;
            if (string.IsNullOrWhiteSpace(curText))
                curText = OcrTrainingSampleTextSimilarity.FallbackFingerprint(parsed);

            var score = OcrTrainingSampleTextSimilarity.DiceCoefficient(s.OcrRawResponse, curText);
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        if (best is null || bestScore < minSim)
            return parsed;

        var (fieldMerged, appliedFields) = TryMergeFromSampleSimilarity(parsed, best, maxFieldConf);
        if (appliedFields.Count == 0)
            return parsed;

        logger.LogInformation(
            "Azure/规则支票 OCR 已应用训练样本纠偏(相似度) SampleId={SampleId} Similarity={Similarity:F3} AppliedFields={Fields}",
            best.Id,
            bestScore,
            string.Join(",", appliedFields));

        return fieldMerged;
    }

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

        return new OcrResultDto(
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
            CompanyName: pick(cAch?.CompanyName, current.CompanyName),
            ExtractedText: current.ExtractedText,
            Iban: current.Iban,
            Bic: current.Bic,
            Diagnostics: current.Diagnostics);
    }

    private static (OcrResultDto merged, List<string> appliedFields) TryMergeFromSampleSimilarity(
        OcrResultDto current,
        OcrTrainingSample s,
        double maxFieldConfidence)
    {
        var appliedFields = new List<string>();
        var cAch = CheckAchExtensionData.Deserialize(s.CorrectAchExtensionJson);

        static double Conf(OcrResultDto d, string k) =>
            d.ConfidenceScores.TryGetValue(k, out var v) ? v : 0.35;

        var checkNumber = current.CheckNumber;
        if (SampleHasCheckNumberDiff(s)
            && !string.IsNullOrWhiteSpace(s.CorrectCheckNumber)
            && Conf(current, "CheckNumber") <= maxFieldConfidence)
        {
            checkNumber = s.CorrectCheckNumber.Trim();
            appliedFields.Add("CheckNumber");
        }

        var amount = current.Amount;
        if (SampleHasAmountDiff(s)
            && s.CorrectAmount.HasValue
            && Conf(current, "Amount") <= maxFieldConfidence)
        {
            amount = s.CorrectAmount.Value;
            appliedFields.Add("Amount");
        }

        var date = current.Date;
        if (SampleHasDateDiff(s)
            && s.CorrectDate.HasValue
            && Conf(current, "Date") <= maxFieldConfidence)
        {
            date = s.CorrectDate.Value;
            if (date.Kind == DateTimeKind.Unspecified)
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            else
                date = date.ToUniversalTime();
            appliedFields.Add("Date");
        }

        var oAch = CheckAchExtensionData.Deserialize(s.OcrAchExtensionJson);
        var achGate = Math.Min(Conf(current, "RoutingNumber"), Conf(current, "AccountNumber"));
        var allowAch = achGate <= maxFieldConfidence + 0.08
                       && oAch != null
                       && cAch != null
                       && !CheckAchExtensionData.EqualsForTraining(oAch, cAch);

        string? pick(string? correct, string? fallback) =>
            !string.IsNullOrWhiteSpace(correct) ? correct.Trim() : fallback;

        string? routing = current.RoutingNumber;
        string? accountNumber = current.AccountNumber;
        string? bankName = current.BankName;
        string? accountHolderName = current.AccountHolderName;
        string? accountAddress = current.AccountAddress;
        string? accountType = current.AccountType;
        string? payTo = current.PayToOrderOf;
        string? forMemo = current.ForMemo;
        string? micrLineRaw = current.MicrLineRaw;
        string? checkNumberMicr = current.CheckNumberMicr;
        string? micrFieldOrderNote = current.MicrFieldOrderNote;
        string? companyName = current.CompanyName;

        if (allowAch && cAch is not null)
        {
            void Track(string field, string? before, string? after)
            {
                if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                    return;
                appliedFields.Add(field);
            }

            var r = pick(cAch.RoutingNumber, current.RoutingNumber);
            if (r is { Length: 9 } && r.All(char.IsDigit))
            {
                routing = r;
                Track("RoutingNumber", current.RoutingNumber, routing);
            }

            accountNumber = pick(cAch.AccountNumber, current.AccountNumber);
            Track("AccountNumber", current.AccountNumber, accountNumber);

            bankName = pick(cAch.BankName, current.BankName);
            Track("BankName", current.BankName, bankName);

            accountHolderName = pick(cAch.AccountHolderName, current.AccountHolderName);
            Track("AccountHolderName", current.AccountHolderName, accountHolderName);

            accountAddress = pick(cAch.AccountAddress, current.AccountAddress);
            Track("AccountAddress", current.AccountAddress, accountAddress);

            accountType = pick(cAch.AccountType, current.AccountType);
            Track("AccountType", current.AccountType, accountType);

            payTo = pick(cAch.PayToOrderOf, current.PayToOrderOf);
            Track("PayToOrderOf", current.PayToOrderOf, payTo);

            forMemo = pick(cAch.ForMemo, current.ForMemo);
            Track("ForMemo", current.ForMemo, forMemo);

            micrLineRaw = pick(cAch.MicrLineRaw, current.MicrLineRaw);
            Track("MicrLineRaw", current.MicrLineRaw, micrLineRaw);

            checkNumberMicr = pick(cAch.CheckNumberMicr, current.CheckNumberMicr);
            Track("CheckNumberMicr", current.CheckNumberMicr, checkNumberMicr);

            micrFieldOrderNote = pick(cAch.MicrFieldOrderNote, current.MicrFieldOrderNote);
            Track("MicrFieldOrderNote", current.MicrFieldOrderNote, micrFieldOrderNote);

            companyName = pick(cAch.CompanyName, current.CompanyName);
            Track("CompanyName", current.CompanyName, companyName);
        }

        var scores = BoostConfidenceSelective(current.ConfidenceScores, appliedFields);

        return (new OcrResultDto(
            CheckNumber: checkNumber,
            Amount: amount,
            Date: date,
            ConfidenceScores: scores,
            RoutingNumber: routing,
            AccountNumber: accountNumber,
            BankName: bankName,
            AccountHolderName: accountHolderName,
            AccountAddress: accountAddress,
            AccountType: accountType,
            PayToOrderOf: payTo,
            ForMemo: forMemo,
            MicrLineRaw: micrLineRaw,
            CheckNumberMicr: checkNumberMicr,
            MicrFieldOrderNote: micrFieldOrderNote,
            CompanyName: companyName,
            ExtractedText: current.ExtractedText,
            Iban: current.Iban,
            Bic: current.Bic,
            Diagnostics: current.Diagnostics), appliedFields);
    }

    private static bool SampleHasCheckNumberDiff(OcrTrainingSample s) =>
        !string.Equals(
            CheckOcrTrainingSampleDiff.Norm(s.OcrCheckNumber),
            CheckOcrTrainingSampleDiff.Norm(s.CorrectCheckNumber),
            StringComparison.OrdinalIgnoreCase);

    private static bool SampleHasAmountDiff(OcrTrainingSample s) =>
        s.OcrAmount != s.CorrectAmount;

    private static bool SampleHasDateDiff(OcrTrainingSample s)
    {
        var od = s.OcrDate?.Date;
        var cd = s.CorrectDate?.Date;
        return od != cd;
    }

    private static Dictionary<string, double> BoostConfidenceSelective(
        Dictionary<string, double> original,
        IReadOnlyCollection<string> appliedFields)
    {
        var d = new Dictionary<string, double>(original, StringComparer.OrdinalIgnoreCase);
        foreach (var key in appliedFields)
        {
            if (d.ContainsKey(key))
                d[key] = Math.Clamp(d[key] + 0.12, 0.0, 0.95);
        }

        return d;
    }

    private static Dictionary<string, double> BoostConfidenceForTrainingMerge(
        Dictionary<string, double> original)
    {
        var d = new Dictionary<string, double>(original, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
                 {
                     "CheckNumber", "CheckNumberMicr", "Amount", "Date", "RoutingNumber", "AccountNumber",
                     "BankName", "AccountHolderName", "AccountAddress", "AccountType", "PayToOrderOf", "CompanyName", "ForMemo",
                     "MicrLineRaw"
                 })
        {
            if (d.ContainsKey(key))
                d[key] = Math.Clamp(d[key] + 0.12, 0.0, 0.95);
        }

        return d;
    }
}
