using System.Text.RegularExpressions;
using CheckPay.Application.Common.Models;

namespace CheckPay.Infrastructure.Services;

/// <summary>Azure Vision Read 支票/扣款全文的后处理解析（几何加权 + 启发式）。</summary>
internal static class CheckOcrVisionReadParser
{
    // MICR 行格式：⑆路由号⑆ ⑈账号⑈ 支票号（或简化版数字序列）
    private static readonly Regex MicrCheckNumberRegex = new(
        @"(?:⑆[0-9⑆⑈ ]+⑈[0-9⑆⑈ ]+\s+|[⑆⑈])([0-9]{4,6})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PrintedCheckNumberRegex = new(
        @"(?:check\s*(?:no\.?|number|#)\s*:?\s*|^|\s)(\d{4,6})(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AmountRegex = new(
        @"\$\s*([\d,]+\.\d{2})\b|([\d,]{1,10}\.\d{2})\b",
        RegexOptions.Compiled);

    private static readonly Regex DateRegex = new(
        @"\b(?:(?:0?[1-9]|1[0-2])[/\-](?:0?[1-9]|[12]\d|3[01])[/\-](?:\d{4}|\d{2})|" +
        @"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BankReferenceLabelRegex = new(
        @"(?i)(?:ref(?:erence)?|trace(?:\s*(?:no\.?|number|#))?|confirmation|confirm(?:ation)?\s*#|trans(?:action)?(?:\s*id|#)?)\s*[:\#]?\s*([A-Z0-9][A-Z0-9\-]{3,48})",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PayToOrderLineRegex = new(
        @"(?i)pay\s+to\s+the\s+order\s+of\s*[:\s]*([^\r\n]{2,200})",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>支票号：MICR 区 + 印刷区双通道，逻辑与旧版一致，优先使用版式区域裁剪文本。</summary>
    public static (string checkNumber, double confidence) ParseCheckNumber(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var micrText = layout.ConcatLinesInRegion(profile.MicrPriorRegion, "\n");
        if (string.IsNullOrWhiteSpace(micrText))
            micrText = TailFallback(layout.FullText, 600);

        var printedRegion = profile.PrintedCheckPriorRegion;
        var printedText = layout.ConcatLinesInRegion(printedRegion, "\n");
        if (string.IsNullOrWhiteSpace(printedText))
            printedText = layout.FullText;

        var micrMatch = MicrCheckNumberRegex.Match(micrText);
        var printedMatch = PrintedCheckNumberRegex.Match(printedText);

        var micrNumber = micrMatch.Success ? micrMatch.Groups[1].Value.Trim() : null;
        var printedNumber = printedMatch.Success ? printedMatch.Groups[1].Value.Trim() : null;

        if (micrNumber != null && printedNumber != null)
        {
            if (micrNumber == printedNumber)
                return (micrNumber, 0.92);
            return (micrNumber, 0.55);
        }

        if (micrNumber != null)
            return (micrNumber, 0.72);
        if (printedNumber != null)
            return (printedNumber, 0.62);

        // 退化：全文再扫一遍
        return ParseCheckNumberFullText(layout.FullText);
    }

    private static (string checkNumber, double confidence) ParseCheckNumberFullText(string text)
    {
        var micrMatch = MicrCheckNumberRegex.Match(text);
        var printedMatch = PrintedCheckNumberRegex.Match(text);
        var micrNumber = micrMatch.Success ? micrMatch.Groups[1].Value.Trim() : null;
        var printedNumber = printedMatch.Success ? printedMatch.Groups[1].Value.Trim() : null;

        if (micrNumber != null && printedNumber != null)
        {
            if (micrNumber == printedNumber)
                return (micrNumber, 0.92);
            return (micrNumber, 0.55);
        }

        if (micrNumber != null)
            return (micrNumber, 0.72);
        if (printedNumber != null)
            return (printedNumber, 0.62);
        return (string.Empty, 0.1);
    }

    public static (decimal amount, double confidence) ParseAmount(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var scored = new List<(decimal amount, double score)>();
        foreach (var line in layout.Lines)
        {
            foreach (Match m in AmountRegex.Matches(line.Text))
            {
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace(",", "");
                if (!decimal.TryParse(raw, out var v) || v <= 0m)
                    continue;

                var hasDollar = m.Groups[1].Success;
                var score = ScoreAmountCandidate(line, hasDollar, profile);
                scored.Add((v, score));
            }
        }

        if (scored.Count == 0)
            return ParseAmountFullText(layout.FullText);

        var distinct = scored.Select(s => s.amount).Distinct().ToList();
        var best = scored
            .OrderByDescending(s => s.score)
            .ThenByDescending(s => s.amount)
            .First();

        var conf = best.score;
        if (distinct.Count > 1)
            conf *= 0.92;
        if (distinct.Count > 3)
            conf *= 0.88;

        return (best.amount, Math.Clamp(conf, 0.18, 0.94));
    }

    private static double ScoreAmountCandidate(ReadOcrLine line, bool hasDollar, CheckOcrParsingProfile profile)
    {
        var s = 0.52;
        if (hasDollar)
            s += 0.12;
        if (profile.AmountPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            s += 0.16;
        if (line.NormCenterY < 0.55)
            s += 0.06;
        if (profile.MicrPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            s -= 0.22;
        return s;
    }

    private static (decimal amount, double confidence) ParseAmountFullText(string text)
    {
        var matches = AmountRegex.Matches(text);
        if (matches.Count == 0)
            return (0m, 0.1);

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

        if (amounts.Count == 0)
            return (0m, 0.1);

        var best = amounts[0];
        var conf = amounts.Count == 1 ? 0.88 : amounts.Count <= 3 ? 0.72 : 0.50;
        return (best, conf);
    }

    public static (DateTime? date, double confidence) ParseDate(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var bestDate = (DateTime?)null;
        var bestScore = 0.0;

        foreach (var line in layout.Lines)
        {
            var m = DateRegex.Match(line.Text);
            if (!m.Success)
                continue;

            if (!DateTime.TryParse(m.Value, out var dt))
                continue;

            var score = 0.62;
            if (profile.DatePriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
                score += 0.18;
            if (line.NormCenterY < 0.45 && line.NormCenterX < 0.6)
                score += 0.06;

            if (score > bestScore)
            {
                bestScore = score;
                bestDate = dt;
            }
        }

        if (bestDate is not null)
            return (bestDate, Math.Clamp(bestScore, 0.2, 0.92));

        return ParseDateFullText(layout.FullText);
    }

    private static (DateTime? date, double confidence) ParseDateFullText(string text)
    {
        var match = DateRegex.Match(text);
        if (!match.Success)
            return (null, 0.1);

        if (DateTime.TryParse(match.Value, out var dt))
            return (dt, 0.82);

        return (null, 0.1);
    }

    public static (string? routing, string? account, string? micrLine, double rtConf, double acConf, double micConf)
        ParseMicrHeuristic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null, null, 0.1, 0.1, 0.1);

        var tailStart = Math.Max(0, text.Length - 400);
        var tail = text[tailStart..];
        var digitsRuns = Regex.Matches(tail, @"\d{4,}")
            .Cast<Match>()
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
            if (run == routing)
                continue;
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

    public static (string? line, double confidence) ParsePayToOrderLine(string text)
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

    public static (string? reference, double confidence) ParseBankReference(string text)
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

    private static string TailFallback(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
