using System.Text.RegularExpressions;
using CheckPay.Application.Common.Models;

namespace CheckPay.Infrastructure.Services;

/// <summary>MICR 启发式解析结果（含 ABA 校验与路由选择策略，供诊断与日志）。</summary>
internal readonly record struct MicrHeuristicParseResult(
    string? RoutingNumber,
    string? AccountNumber,
    string? MicrLineRaw,
    double RoutingConfidence,
    double AccountConfidence,
    double MicrLineConfidence,
    bool? RoutingAbaChecksumValid,
    string RoutingSelectionMode);

/// <summary>Azure Vision Read 支票/扣款全文的后处理解析（几何加权 + 启发式）。</summary>
internal static class CheckOcrVisionReadParser
{
    private static readonly Regex MicrTripleDigitsRegex = new(
        @"(\d{9})\s+(\d{8,17})\s+(\d{4,6})\s*$",
        RegexOptions.Compiled);

    private static readonly Regex TailDigitRunsRegex = new(@"\d{4,}", RegexOptions.Compiled);

    // MICR 行格式：⑆路由号⑆ ⑈账号⑈ 支票号（或简化版数字序列）
    private static readonly Regex MicrCheckNumberRegex = new(
        @"(?:⑆[0-9⑆⑈ ]+⑈[0-9⑆⑈ ]+\s+|[⑆⑈])([0-9]{4,6})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PrintedCheckNumberRegex = new(
        @"(?:check\s*(?:no\.?|number|#)\s*:?\s*|^|\s)(\d{4,6})(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PrintedCheckNumberLineRegex = new(
        @"(?:\bcheck\s*(?:no\.?|number|#)\s*[:#]?\s*)?\#?(?<num>\d{4,8})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    private static readonly Regex AddressLineRegex = new(
        @"\b\d{1,6}\s+[A-Z0-9][A-Z0-9\s\.\-#]{3,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NameTokenRegex = new(
        @"\b[A-Z][A-Za-z'\-]{1,}\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> BankNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "pay", "order", "memo", "check", "routing", "account", "amount", "dollars"
    };

    private static readonly HashSet<string> AddressStreetTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "st", "street", "ave", "avenue", "rd", "road", "dr", "drive", "blvd", "boulevard",
        "ln", "lane", "way", "court", "ct", "suite", "ste", "apt", "unit", "p.o.", "po", "box"
    };

    public static MicrHeuristicParseResult ParseMicrHeuristic(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var regionLines = layout.Lines
            .Where(line => profile.MicrPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            .OrderBy(line => line.NormTop)
            .ToList();
        if (regionLines.Count > 0)
        {
            var regionText = string.Join("\n", regionLines.Select(line => line.Text));
            var parsedRegion = ParseMicrHeuristic(regionText);
            if (parsedRegion.RoutingNumber is { Length: 9 } && parsedRegion.RoutingAbaChecksumValid == true)
                return parsedRegion with { RoutingSelectionMode = $"{parsedRegion.RoutingSelectionMode}_region" };

            var bottomLineText = string.Join("\n", regionLines.TakeLast(Math.Min(4, regionLines.Count)).Select(line => line.Text));
            var parsedBottom = ParseMicrHeuristic(bottomLineText);
            if (parsedBottom.RoutingNumber is { Length: 9 } && parsedBottom.RoutingAbaChecksumValid == true)
                return parsedBottom with { RoutingSelectionMode = $"{parsedBottom.RoutingSelectionMode}_bottom" };
        }

        return ParseMicrHeuristic(layout.FullText);
    }

    public static MicrHeuristicParseResult ParseMicrHeuristicBottomBand(ReadOcrLayout layout, double minNormCenterY = 0.78)
    {
        var bottomLines = layout.Lines
            .Where(line => line.NormCenterY >= minNormCenterY)
            .OrderBy(line => line.NormTop)
            .Select(line => line.Text)
            .ToList();
        if (bottomLines.Count == 0)
            return new MicrHeuristicParseResult(null, null, null, 0.1, 0.1, 0.1, null, "bottom_band_empty");

        var parsed = ParseMicrHeuristic(string.Join("\n", bottomLines));
        return parsed with { RoutingSelectionMode = $"{parsed.RoutingSelectionMode}_bottom_band" };
    }

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
        var micrNumber = micrMatch.Success ? micrMatch.Groups[1].Value.Trim() : null;
        var printedCandidate = PickBestPrintedCheckCandidate(layout, profile, printedText);
        var printedNumber = printedCandidate.number;
        var printedScore = printedCandidate.score;

        if (micrNumber != null && printedNumber != null)
        {
            if (micrNumber == printedNumber)
                return (micrNumber, 0.94);
            if (printedScore >= 0.78)
                return (printedNumber, Math.Clamp(printedScore, 0.66, 0.88));
            return (micrNumber, 0.55);
        }

        if (micrNumber != null)
            return (micrNumber, 0.72);
        if (printedNumber != null)
            return (printedNumber, Math.Clamp(printedScore, 0.62, 0.88));

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

    private static (string? number, double score) PickBestPrintedCheckCandidate(ReadOcrLayout layout, CheckOcrParsingProfile profile, string printedText)
    {
        var candidates = new List<(string number, double score)>();
        var printedRegion = profile.PrintedCheckPriorRegion;
        var micrRegion = profile.MicrPriorRegion;

        foreach (var line in layout.Lines)
        {
            foreach (Match match in PrintedCheckNumberLineRegex.Matches(line.Text))
            {
                if (!match.Success)
                    continue;

                var number = match.Groups["num"].Value.Trim();
                if (number.Length is < 4 or > 8)
                    continue;

                var score = 0.30;
                if (printedRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
                    score += 0.24;
                if (line.NormCenterX >= 0.70)
                    score += 0.18;
                if (line.NormCenterY <= 0.30)
                    score += 0.14;
                if (Regex.IsMatch(line.Text, @"(?i)\bcheck\s*(?:no\.?|number|#)\b"))
                    score += 0.16;
                if (Regex.IsMatch(line.Text, @"[$]|(?:\d{1,3},)?\d+\.\d{2}"))
                    score -= 0.26;
                if (DateRegex.IsMatch(line.Text))
                    score -= 0.22;
                if (micrRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
                    score -= 0.20;

                candidates.Add((number, score));
            }
        }

        if (candidates.Count > 0)
        {
            var best = candidates
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.number.Length)
                .First();
            return (best.number, Math.Clamp(best.score, 0.20, 0.92));
        }

        var printedMatch = PrintedCheckNumberRegex.Match(printedText);
        if (printedMatch.Success)
            return (printedMatch.Groups[1].Value.Trim(), 0.62);

        return (null, 0.1);
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

    public static MicrHeuristicParseResult ParseMicrHeuristic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new MicrHeuristicParseResult(null, null, null, 0.1, 0.1, 0.1, null, "empty");

        text = NormalizeMicrLikeText(text);
        var tailStart = Math.Max(0, text.Length - 400);
        var tail = text[tailStart..];

        // 1) 底部行常见「路由 账号 支票号」三连数字（E13B 常被读成空格分隔）
        var lines = tail.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var li = lines.Length - 1; li >= 0; li--)
        {
            var line = lines[li].Trim();
            if (line.Length < 22)
                continue;

            var m = MicrTripleDigitsRegex.Match(line);
            if (!m.Success)
                continue;

            var rt = m.Groups[1].Value;
            if (!AbaRoutingNumberValidator.IsValid(rt))
                continue;

            var ac = m.Groups[2].Value;
            var micrLine = $"{rt} {ac} {m.Groups[3].Value}".Trim();
            return new MicrHeuristicParseResult(rt, ac, micrLine, 0.82, 0.74, 0.78, true, "triple_line");
        }

        // 2) 尾部纯数字序列上滑动窗口：优先通过 ABA 校验的 9 位，且取最靠右（更接近 MICR 物理位置）
        var digitTail = new string(tail.Where(char.IsDigit).ToArray());
        var abaWindows = new List<(string Routing, int Start)>();
        for (var i = 0; i + 9 <= digitTail.Length; i++)
        {
            var slice = digitTail.Substring(i, 9);
            if (AbaRoutingNumberValidator.IsValid(slice))
                abaWindows.Add((slice, i));
        }

        if (abaWindows.Count > 0)
        {
            var best = abaWindows.MaxBy(x => x.Start);
            var routing = best.Routing;
            var digitsRuns = TailDigitRunsRegex.Matches(tail).Cast<Match>().Select(x => x.Value).ToList();
            string? account = null;
            foreach (var run in digitsRuns.OrderByDescending(r => r.Length))
            {
                if (string.Equals(run, routing, StringComparison.Ordinal))
                    continue;
                if (run.Length >= 8)
                {
                    account = run;
                    break;
                }
            }

            var micrLine = string.Join(" ", digitsRuns.TakeLast(5));
            return new MicrHeuristicParseResult(
                routing,
                account,
                micrLine.Length > 0 ? micrLine : null,
                0.72,
                account != null ? 0.52 : 0.28,
                micrLine.Length > 0 ? 0.62 : 0.22,
                true,
                "aba_sliding_window");
        }

        // 3) 退化：原「首个 9 位数字串 + 最长 ≥8 位」逻辑（可能未通过 ABA）
        var legacyRuns = TailDigitRunsRegex.Matches(tail).Cast<Match>().Select(x => x.Value).ToList();
        string? legacyRouting = null;
        foreach (var run in legacyRuns.Where(r => r.Length == 9))
        {
            legacyRouting = run;
            break;
        }

        string? legacyAccount = null;
        foreach (var run in legacyRuns.OrderByDescending(r => r.Length))
        {
            if (string.Equals(run, legacyRouting, StringComparison.Ordinal))
                continue;
            if (run.Length >= 8)
            {
                legacyAccount = run;
                break;
            }
        }

        var legacyMicr = string.Join(" ", legacyRuns.TakeLast(5));
        var abaOk = legacyRouting != null && AbaRoutingNumberValidator.IsValid(legacyRouting);
        return new MicrHeuristicParseResult(
            legacyRouting,
            legacyAccount,
            legacyMicr.Length > 0 ? legacyMicr : null,
            legacyRouting != null ? abaOk ? 0.58 : 0.42 : 0.1,
            legacyAccount != null ? 0.38 : 0.1,
            legacyMicr.Length > 0 ? 0.35 : 0.1,
            legacyRouting == null ? null : abaOk,
            "digit_runs_legacy");
    }

    /// <summary>尾部窗口内 <c>\d{4,}</c> 匹配个数，用于诊断 MICR 区是否被 Read 切成多段。</summary>
    public static int CountTailDigitRuns(string text, int tailMaxChars = 400)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var tail = text.Length <= tailMaxChars ? text : text[^tailMaxChars..];
        return TailDigitRunsRegex.Matches(tail).Count;
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

    public static (string? bankName, double confidence) ParseBankName(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var candidates = layout.Lines
            .Where(line => profile.BankNamePriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            .Select(line => (line.Text, ScoreBankNameCandidate(line.Text, line.NormCenterY)))
            .Where(x => x.Item2 > 0.3)
            .OrderByDescending(x => x.Item2)
            .ToList();

        if (candidates.Count == 0)
            return (null, 0.1);

        return (NormalizeSpaces(candidates[0].Text), Math.Clamp(candidates[0].Item2, 0.2, 0.9));
    }

    public static (string? accountHolder, double confidence) ParseAccountHolderName(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var candidates = layout.Lines
            .Where(line => profile.AccountHolderPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            .Select(line => (line.Text, ScoreAccountHolderCandidate(line.Text, line.NormCenterY)))
            .Where(x => x.Item2 > 0.35)
            .OrderByDescending(x => x.Item2)
            .ToList();

        if (candidates.Count == 0)
            return (null, 0.1);

        return (NormalizeSpaces(candidates[0].Text), Math.Clamp(candidates[0].Item2, 0.2, 0.88));
    }

    public static (string? accountAddress, double confidence) ParseAccountAddress(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var regionLines = layout.Lines
            .Where(line => profile.AccountAddressPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            .OrderBy(line => line.NormTop)
            .ToList();
        if (regionLines.Count == 0)
            return (null, 0.1);

        var scored = regionLines
            .Select(line => (line.Text, ScoreAddressCandidate(line.Text)))
            .Where(x => x.Item2 > 0.38)
            .OrderByDescending(x => x.Item2)
            .ToList();
        if (scored.Count == 0)
            return (null, 0.1);

        var top = scored[0].Text;
        var topNorm = regionLines.FirstOrDefault(l => string.Equals(l.Text, top, StringComparison.Ordinal));
        if (topNorm is null)
            return (NormalizeSpaces(top), Math.Clamp(scored[0].Item2, 0.2, 0.84));

        var block = regionLines
            .Where(l => Math.Abs(l.NormCenterY - topNorm.NormCenterY) <= 0.12)
            .Select(l => l.Text)
            .Distinct()
            .Take(3)
            .ToList();
        var merged = NormalizeSpaces(string.Join(", ", block));
        return (merged, Math.Clamp(scored[0].Item2 + (block.Count > 1 ? 0.06 : 0.0), 0.22, 0.88));
    }

    private static double ScoreBankNameCandidate(string text, double normY)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0;
        var normalized = NormalizeSpaces(text);
        var lower = normalized.ToLowerInvariant();
        var score = 0.3;
        if (lower.Contains("bank", StringComparison.Ordinal))
            score += 0.42;
        if (lower.Contains("credit union", StringComparison.Ordinal))
            score += 0.3;
        if (lower.Contains("national", StringComparison.Ordinal) || lower.Contains("trust", StringComparison.Ordinal))
            score += 0.08;
        if (BankNoiseTokens.Any(lower.Contains))
            score -= 0.2;
        if (normalized.Length is >= 6 and <= 48)
            score += 0.08;
        if (normY < 0.18)
            score += 0.06;
        return score;
    }

    private static double ScoreAccountHolderCandidate(string text, double normY)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0;
        var normalized = NormalizeSpaces(text);
        var lower = normalized.ToLowerInvariant();
        var score = 0.3;
        if (lower.Contains("pay to the order", StringComparison.Ordinal) || lower.Contains("memo", StringComparison.Ordinal))
            return 0.0;
        var tokenCount = NameTokenRegex.Matches(normalized).Count;
        if (tokenCount >= 2)
            score += 0.26;
        if (tokenCount >= 3)
            score += 0.08;
        if (AddressLineRegex.IsMatch(normalized))
            score -= 0.24;
        if (Regex.IsMatch(normalized, @"\d{3,}"))
            score -= 0.12;
        if (normalized.Length is >= 5 and <= 64)
            score += 0.08;
        if (normY is > 0.2 and < 0.58)
            score += 0.06;
        return score;
    }

    private static double ScoreAddressCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0;
        var normalized = NormalizeSpaces(text);
        var lower = normalized.ToLowerInvariant();
        var score = 0.26;
        if (AddressLineRegex.IsMatch(normalized))
            score += 0.36;
        if (AddressStreetTokens.Any(token => Regex.IsMatch(lower, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase)))
            score += 0.2;
        if (Regex.IsMatch(normalized, @"\b[A-Z]{2}\s+\d{5}(?:-\d{4})?\b"))
            score += 0.16;
        if (lower.Contains("bank", StringComparison.Ordinal) && !Regex.IsMatch(normalized, @"\d"))
            score -= 0.15;
        if (normalized.Length is < 8 or > 90)
            score -= 0.08;
        return score;
    }

    private static string NormalizeSpaces(string text) =>
        Regex.Replace(text.Trim(), @"\s{2,}", " ");

    private static string NormalizeMicrLikeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text
            .Replace('O', '0')
            .Replace('o', '0')
            .Replace('I', '1')
            .Replace('l', '1')
            .Replace('S', '5')
            .Replace('s', '5')
            .Replace('B', '8')
            .Replace('|', '1');
    }

    private static string TailFallback(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
