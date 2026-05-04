using System.Globalization;
using System.Text;
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
    private static readonly object StatsLock = new();
    private static DateTime _statsWindowStartUtc = DateTime.UtcNow;
    private static DateTime _statsLastFlushUtc = DateTime.UtcNow;
    private static int _statsTotalRequests;
    private static int _statsMatchedRequests;
    private static int _statsChangedFieldTotal;
    private static readonly Dictionary<string, int> StatsFieldHits = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CandidateSelection(
        List<OcrTrainingSample> Samples,
        string? ClusterKey,
        int MaturedSampleCount,
        int EligibleClusterCount);

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
        var selection = SelectClusterCandidates(parsed, ordered);
        if (selection.Samples.Count == 0)
        {
            RecordAndMaybeFlushSummary(matched: false, changedFields: []);
            return parsed;
        }

        foreach (var s in selection.Samples)
        {
            if (!CheckOcrTrainingSampleDiff.HasStructuredDiff(s))
                continue;
            if (!IsStrongMatch(parsed, s))
                continue;

            var merged = MergeFromSample(parsed, s);
            var changeStats = BuildChangeStats(parsed, merged);
            logger.LogInformation(
                "Azure/规则支票 OCR 已应用训练样本纠偏(强匹配) SampleId={SampleId} CreatedAt={CreatedAt:O} ClusterKey={ClusterKey} ChangedCount={ChangedCount} ChangedFields={ChangedFields} ChangedPairs={ChangedPairs}",
                s.Id,
                s.CreatedAt,
                selection.ClusterKey,
                changeStats.ChangedCount,
                string.Join(",", changeStats.ChangedFields),
                changeStats.ChangedPairs);
            RecordAndMaybeFlushSummary(matched: true, changedFields: changeStats.ChangedFields);
            return merged;
        }

        var mode = configuration["Ocr:CheckAzureTrainingCorrectionMode"]?.Trim() ?? "StrongOnly";
        if (!string.Equals(mode, "Similarity", StringComparison.OrdinalIgnoreCase))
        {
            RecordAndMaybeFlushSummary(matched: false, changedFields: []);
            return parsed;
        }

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
        foreach (var s in selection.Samples)
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
        {
            RecordAndMaybeFlushSummary(matched: false, changedFields: []);
            return parsed;
        }

        var (fieldMerged, appliedFields) = TryMergeFromSampleSimilarity(parsed, best, maxFieldConf);
        if (appliedFields.Count == 0)
        {
            RecordAndMaybeFlushSummary(matched: false, changedFields: []);
            return parsed;
        }

        logger.LogInformation(
            "Azure/规则支票 OCR 已应用训练样本纠偏(相似度) SampleId={SampleId} Similarity={Similarity:F3} ClusterKey={ClusterKey} CandidateSamples={CandidateSamples} MaturedSamples={MaturedSamples} EligibleClusters={EligibleClusters} ChangedCount={ChangedCount} ChangedFields={Fields} ChangedPairs={ChangedPairs}",
            best.Id,
            bestScore,
            selection.ClusterKey,
            selection.Samples.Count,
            selection.MaturedSampleCount,
            selection.EligibleClusterCount,
            appliedFields.Count,
            string.Join(",", appliedFields),
            BuildChangedPairs(parsed, fieldMerged, appliedFields));
        RecordAndMaybeFlushSummary(matched: true, changedFields: appliedFields);

        return fieldMerged;
    }

    private void RecordAndMaybeFlushSummary(bool matched, IReadOnlyCollection<string> changedFields)
    {
        var enabled = true;
        if (bool.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionSummaryEnabled"], out var en))
            enabled = en;
        if (!enabled)
            return;

        var flushMinutes = 15;
        if (int.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionSummaryFlushMinutes"], out var cfg) && cfg > 0)
            flushMinutes = cfg;

        var now = DateTime.UtcNow;
        bool shouldFlush;
        string? summaryPayload = null;

        lock (StatsLock)
        {
            _statsTotalRequests++;
            if (matched)
            {
                _statsMatchedRequests++;
                _statsChangedFieldTotal += changedFields.Count;
                foreach (var field in changedFields)
                {
                    if (StatsFieldHits.TryGetValue(field, out var count))
                        StatsFieldHits[field] = count + 1;
                    else
                        StatsFieldHits[field] = 1;
                }
            }

            shouldFlush = (now - _statsLastFlushUtc).TotalMinutes >= flushMinutes;
            if (!shouldFlush || _statsTotalRequests == 0)
                return;

            var hitRate = _statsMatchedRequests / (double)_statsTotalRequests;
            var avgChanged = _statsMatchedRequests == 0 ? 0.0 : _statsChangedFieldTotal / (double)_statsMatchedRequests;
            var topFields = StatsFieldHits
                .OrderByDescending(x => x.Value)
                .Take(5)
                .Select(x => $"{x.Key}:{x.Value}")
                .ToArray();

            var sb = new StringBuilder();
            sb.Append("WindowStart=").Append(_statsWindowStartUtc.ToString("O", CultureInfo.InvariantCulture));
            sb.Append(" WindowEnd=").Append(now.ToString("O", CultureInfo.InvariantCulture));
            sb.Append(" Total=").Append(_statsTotalRequests);
            sb.Append(" Matched=").Append(_statsMatchedRequests);
            sb.Append(" HitRate=").Append(hitRate.ToString("P2", CultureInfo.InvariantCulture));
            sb.Append(" AvgChangedFields=").Append(avgChanged.ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(" TopFields=").Append(topFields.Length == 0 ? "-" : string.Join(",", topFields));
            summaryPayload = sb.ToString();

            _statsWindowStartUtc = now;
            _statsLastFlushUtc = now;
            _statsTotalRequests = 0;
            _statsMatchedRequests = 0;
            _statsChangedFieldTotal = 0;
            StatsFieldHits.Clear();
        }

        if (!string.IsNullOrWhiteSpace(summaryPayload))
        {
            logger.LogInformation(
                "CheckOcrTrainingCorrectionSummary {Summary}",
                summaryPayload);
        }
    }

    private CandidateSelection SelectClusterCandidates(
        OcrResultDto parsed,
        List<OcrTrainingSample> ordered)
    {
        var minSamples = 3;
        if (int.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionClusterMinSamples"], out var configuredMin) && configuredMin > 0)
            minSamples = configuredMin;

        var minAgeMinutes = 30;
        if (int.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionSampleMinAgeMinutes"], out var configuredAge) && configuredAge >= 0)
            minAgeMinutes = configuredAge;

        var requireTemplateMatch = true;
        if (bool.TryParse(configuration["Ocr:CheckAzureTrainingCorrectionRequireTemplateMatch"], out var strict))
            requireTemplateMatch = strict;

        var thresholdUtc = DateTime.UtcNow.AddMinutes(-minAgeMinutes);
        var matured = ordered.Where(s => s.CreatedAt <= thresholdUtc).ToList();
        if (matured.Count == 0)
            return new CandidateSelection([], null, 0, 0);

        var parsedTemplateId = TryGetParsedTemplateId(parsed.Diagnostics);
        var parsedCluster = BuildClusterKey(parsedTemplateId, parsed.RoutingNumber);

        var grouped = matured
            .GroupBy(s => BuildClusterKey(s.OcrCheckTemplateId, CheckAchExtensionData.Deserialize(s.OcrAchExtensionJson)?.RoutingNumber))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() >= minSamples)
            .ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(parsedCluster) && grouped.TryGetValue(parsedCluster, out var clusterSamples))
            return new CandidateSelection(clusterSamples, parsedCluster, matured.Count, grouped.Count);

        if (requireTemplateMatch)
            return new CandidateSelection([], parsedCluster, matured.Count, grouped.Count);

        var fallback = grouped.Values.SelectMany(v => v).OrderByDescending(s => s.CreatedAt).ToList();
        return new CandidateSelection(fallback, parsedCluster, matured.Count, grouped.Count);
    }

    private static Guid? TryGetParsedTemplateId(IReadOnlyDictionary<string, string>? diagnostics)
    {
        if (diagnostics == null)
            return null;
        if (!diagnostics.TryGetValue("template_id", out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string? BuildClusterKey(Guid? templateId, string? routingNumber)
    {
        if (templateId.HasValue)
            return $"tpl:{templateId.Value:D}";
        var routing = CheckOcrTrainingSampleDiff.NormDigits(routingNumber);
        return routing == null ? null : $"rtn:{routing}";
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

        var mergedBankStrong = pick(cAch?.BankName, current.BankName);
        var mergedCompanyStrong = pick(cAch?.CompanyName, current.CompanyName);
        if (CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder(mergedCompanyStrong, mergedBankStrong))
            mergedCompanyStrong = current.CompanyName;

        return new OcrResultDto(
            CheckNumber: checkNumber,
            Amount: amount,
            Date: date,
            ConfidenceScores: BoostConfidenceForTrainingMerge(current.ConfidenceScores),
            RoutingNumber: routing,
            AccountNumber: pick(cAch?.AccountNumber, current.AccountNumber),
            BankName: mergedBankStrong,
            AccountHolderName: pick(cAch?.AccountHolderName, current.AccountHolderName),
            AccountAddress: pick(cAch?.AccountAddress, current.AccountAddress),
            AccountType: pick(cAch?.AccountType, current.AccountType),
            PayToOrderOf: pick(cAch?.PayToOrderOf, current.PayToOrderOf),
            ForMemo: pick(cAch?.ForMemo, current.ForMemo),
            MicrLineRaw: pick(cAch?.MicrLineRaw, current.MicrLineRaw),
            CheckNumberMicr: pick(cAch?.CheckNumberMicr, current.CheckNumberMicr),
            MicrFieldOrderNote: pick(cAch?.MicrFieldOrderNote, current.MicrFieldOrderNote),
            CompanyName: mergedCompanyStrong,
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

            var proposedCompany = pick(cAch.CompanyName, current.CompanyName);
            // 训练样本里 CompanyName 误存为付款行时，勿覆盖当前解析
            if (CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder(proposedCompany, bankName))
                proposedCompany = current.CompanyName;
            companyName = proposedCompany;
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

    private static (int ChangedCount, List<string> ChangedFields, string ChangedPairs) BuildChangeStats(
        OcrResultDto before,
        OcrResultDto after)
    {
        var fields = new List<string>();
        var pairs = new List<string>();
        Track("CheckNumber", before.CheckNumber, after.CheckNumber);
        Track("Amount", before.Amount.ToString("0.00", CultureInfo.InvariantCulture), after.Amount.ToString("0.00", CultureInfo.InvariantCulture));
        Track("Date", before.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), after.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Track("RoutingNumber", before.RoutingNumber, after.RoutingNumber);
        Track("AccountNumber", before.AccountNumber, after.AccountNumber);
        Track("BankName", before.BankName, after.BankName);
        Track("AccountHolderName", before.AccountHolderName, after.AccountHolderName);
        Track("AccountAddress", before.AccountAddress, after.AccountAddress);
        Track("AccountType", before.AccountType, after.AccountType);
        Track("PayToOrderOf", before.PayToOrderOf, after.PayToOrderOf);
        Track("ForMemo", before.ForMemo, after.ForMemo);
        Track("MicrLineRaw", before.MicrLineRaw, after.MicrLineRaw);
        Track("CheckNumberMicr", before.CheckNumberMicr, after.CheckNumberMicr);
        Track("MicrFieldOrderNote", before.MicrFieldOrderNote, after.MicrFieldOrderNote);
        Track("CompanyName", before.CompanyName, after.CompanyName);

        return (fields.Count, fields, string.Join(" | ", pairs));

        void Track(string field, string? b, string? a)
        {
            if (string.Equals(NormForStats(b), NormForStats(a), StringComparison.OrdinalIgnoreCase))
                return;
            fields.Add(field);
            pairs.Add($"{field}:{Safe(b)}->{Safe(a)}");
        }
    }

    private static string BuildChangedPairs(OcrResultDto before, OcrResultDto after, IReadOnlyCollection<string> fields)
    {
        if (fields.Count == 0)
            return string.Empty;

        var pairs = new List<string>();
        foreach (var field in fields)
        {
            var b = GetFieldValue(before, field);
            var a = GetFieldValue(after, field);
            if (!string.Equals(NormForStats(b), NormForStats(a), StringComparison.OrdinalIgnoreCase))
                pairs.Add($"{field}:{Safe(b)}->{Safe(a)}");
        }
        return string.Join(" | ", pairs);
    }

    private static string? GetFieldValue(OcrResultDto dto, string field) =>
        field switch
        {
            "CheckNumber" => dto.CheckNumber,
            "Amount" => dto.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            "Date" => dto.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "RoutingNumber" => dto.RoutingNumber,
            "AccountNumber" => dto.AccountNumber,
            "BankName" => dto.BankName,
            "AccountHolderName" => dto.AccountHolderName,
            "AccountAddress" => dto.AccountAddress,
            "AccountType" => dto.AccountType,
            "PayToOrderOf" => dto.PayToOrderOf,
            "ForMemo" => dto.ForMemo,
            "MicrLineRaw" => dto.MicrLineRaw,
            "CheckNumberMicr" => dto.CheckNumberMicr,
            "MicrFieldOrderNote" => dto.MicrFieldOrderNote,
            "CompanyName" => dto.CompanyName,
            _ => null
        };

    private static string NormForStats(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
