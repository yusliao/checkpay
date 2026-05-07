using System.Text.RegularExpressions;
using CheckPay.Application.Common.Models;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 多源金额（Vision 主解析、ROI 二次 Read、DI Number/Word、全文大写行）仲裁与数字化「分」粘入整数美元的简易修复。
/// </summary>
internal static class CheckOcrAmountArbitration
{
    private static readonly Regex StuckCourtesyAfterDollarRegex = new(
        @"\$\D*[\d,]{6,}",
        RegexOptions.Compiled);

    /// <summary>
    /// 形如 604805.05：整数部末两位实为「分」，被 OCR 粘在美元整数后（604805 与 .05 同源）。
    /// 仅在存在旁证时采纳（全文大写金额、DI Word、DI Number 或 ROI）。
    /// </summary>
    public static bool TryRepairEmbeddedCentsInScaledDollar(decimal amount, out decimal repaired)
    {
        repaired = amount;
        if (amount is <= 0m or >= 100_000_000m)
            return false;

        var whole = (long)Math.Truncate(amount);
        var frac = amount - whole;
        if (frac < 0.009m || frac > 0.991m)
            return false;

        var cents = (int)Math.Round(frac * 100m, MidpointRounding.AwayFromZero);
        if (cents is < 1 or > 99)
            return false;
        if (whole < 1_000L)
            return false;
        if (whole % 100 != cents)
            return false;

        repaired = whole / 100m;
        return repaired > 0m && Math.Abs(repaired - amount) > 0.02m;
    }

    public static bool LayoutSuggestsDigitAdhesion(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var micr = profile.MicrPriorRegion;
        foreach (var line in layout.Lines)
        {
            if (micr?.Contains(line.NormCenterX, line.NormCenterY) == true)
                continue;
            if (line.NormCenterY >= 0.72)
                continue;
            if (StuckCourtesyAfterDollarRegex.IsMatch(line.Text))
                return true;
        }

        return false;
    }

    public static bool ShouldTriggerRoiSecondPass(
        AmountRoiSecondPassMode mode,
        double visionConfTrigger,
        decimal amount,
        double amountConf,
        ReadOcrLayout layout,
        CheckOcrParsingProfile profile,
        string rawText)
    {
        if (layout.ImageWidth <= 32 || layout.ImageHeight <= 32)
            return false;
        if (mode == AmountRoiSecondPassMode.Always)
            return true;
        if (amountConf < visionConfTrigger)
            return true;
        if (LayoutSuggestsDigitAdhesion(layout, profile))
            return true;
        if (amount > 0
            && AzureOcrService.TryParseBestWrittenAmountFromCheckFullText(rawText, out var w)
            && w > 0m
            && amount >= w * 25m
            && amount - w >= 100m)
            return true;
        return false;
    }

    internal static AmountRoiSecondPassMode ParseAmountRoiSecondPassMode(string? raw) =>
        string.Equals(raw, "Always", StringComparison.OrdinalIgnoreCase)
            ? AmountRoiSecondPassMode.Always
            : AmountRoiSecondPassMode.OnDemand;

    /// <summary>是否用 ROI 二次解析结果替换首轮金额。</summary>
    public static bool ShouldPreferRoiAmount(
        decimal primary,
        double primaryConf,
        decimal roiAmount,
        double roiConf,
        string rawText)
    {
        if (roiAmount <= 0m)
            return false;
        if (Math.Abs(roiAmount - primary) <= 0.02m)
            return false;

        if (AzureOcrService.TryParseBestWrittenAmountFromCheckFullText(rawText, out var w) && w > 0m)
        {
            if (Math.Abs(w - roiAmount) <= 0.05m && Math.Abs(w - primary) > 0.5m)
                return true;
        }

        if (TryRepairEmbeddedCentsInScaledDollar(primary, out var embedded)
            && Math.Abs(embedded - roiAmount) <= 0.02m)
            return true;

        if (roiConf >= primaryConf + 0.04
            && primary >= 5_000m
            && roiAmount < primary / 4m
            && roiAmount >= 5m)
            return true;

        return false;
    }

    public static bool TryApplyEmbeddedCentsRepairWithConfirmation(
        ref decimal amount,
        ref double conf,
        PrebuiltCheckStructuredFields di,
        string rawText,
        IDictionary<string, string> diagnostics,
        PrebuiltCheckStructuredFields? roiCropDi = null)
    {
        if (!TryRepairEmbeddedCentsInScaledDollar(amount, out var repaired))
            return false;

        var ok = false;
        if (di.WordAmountParsed is { } w && Math.Abs(w - repaired) <= 0.02m)
            ok = true;
        if (!ok && di.NumberAmount is { } n && Math.Abs(n - repaired) <= 0.02m)
            ok = true;
        if (!ok && roiCropDi?.WordAmountParsed is { } rw && Math.Abs(rw - repaired) <= 0.02m)
            ok = true;
        if (!ok && roiCropDi?.NumberAmount is { } rn && Math.Abs(rn - repaired) <= 0.02m)
            ok = true;
        if (!ok && AzureOcrService.TryParseBestWrittenAmountFromCheckFullText(rawText, out var vw) && vw > 0m
                 && Math.Abs(vw - repaired) <= 0.02m)
            ok = true;

        if (!ok)
            return false;

        amount = repaired;
        conf = Math.Clamp(conf + 0.06, 0.55, 0.88);
        diagnostics["amount_embedded_cents_repair"] = "applied";
        return true;
    }

    /// <summary>
    /// 合并路径（DI 融合与书面 augmentation 之后）：若至少两路金额在 0.02 内一致且与当前不同，取该簇中高置信度代表。
    /// </summary>
    public static bool TryApplyMultiSourceConsensus(
        ref decimal amount,
        ref double conf,
        PrebuiltCheckStructuredFields di,
        string rawText,
        IDictionary<string, string> diagnostics,
        PrebuiltCheckStructuredFields? roiCropDi = null)
    {
        var candidates = new List<(decimal v, double c, string src)>();
        void add(decimal v, double c, string s)
        {
            if (v <= 0m)
                return;
            candidates.Add((v, c, s));
        }

        add(amount, conf, "merged");
        if (di.NumberAmount is { } n)
            add(n, di.NumberAmountConfidence, "di_number");
        if (di.WordAmountParsed is { } w)
            add(w, di.WordAmountConfidence, "di_word");
        if (roiCropDi?.NumberAmount is { } crn && crn > 0m)
            add(crn, Math.Clamp(roiCropDi.NumberAmountConfidence * 0.92, 0.48, 0.86), "roi_crop_di_number");
        if (roiCropDi?.WordAmountParsed is { } crw && crw > 0m)
            add(crw, Math.Clamp(roiCropDi.WordAmountConfidence * 0.92, 0.48, 0.86), "roi_crop_di_word");
        if (AzureOcrService.TryParseBestWrittenAmountFromCheckFullText(rawText, out var vw) && vw > 0m)
            add(vw, 0.52, "vision_written");

        if (candidates.Count < 2)
            return false;

        (decimal v, double c, string src)? bestRepresentative = null;
        var bestPeers = 0;
        var bestScore = -1.0;

        foreach (var seed in candidates)
        {
            var cluster = candidates.Where(x => Math.Abs(x.v - seed.v) <= 0.02m).ToList();
            if (cluster.Count < 2)
                continue;

            var top = cluster.OrderByDescending(x => x.c).First();
            var score = cluster.Sum(x => x.c) + cluster.Count * 0.08;
            if (score > bestScore || (Math.Abs(score - bestScore) < 0.0001 && cluster.Count > bestPeers))
            {
                bestScore = score;
                bestPeers = cluster.Count;
                bestRepresentative = top;
            }
        }

        if (bestRepresentative is not { } rep)
            return false;
        if (Math.Abs(rep.v - amount) <= 0.02m)
            return false;

        amount = rep.v;
        conf = Math.Clamp(Math.Max(conf, rep.c) + 0.04, 0.55, 0.88);
        diagnostics["amount_multisource_arbitration"] = "consensus_" + bestPeers + "_" + rep.src;
        return true;
    }
}
