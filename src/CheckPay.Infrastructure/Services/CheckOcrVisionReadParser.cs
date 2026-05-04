using System.Globalization;
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

    // MICR 行格式：⑆路由号⑆ ⑈账号⑈ 支票号（或简化版数字序列）；⑈ 前可为 4~17 位（含行末长 external check）
    private static readonly Regex MicrCheckNumberRegex = new(
        @"(?:⑆[0-9⑆⑈ ]+⑈[0-9⑆⑈ ]+\s+|[⑆⑈])([0-9]{4,17})\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>E13B transit 符号（U+2446）包裹的 9 位路由号（允许符号与数字间少量空格）。</summary>
    private static readonly Regex E13bTransitRoutingRegex = new(@"⑆\s*(\d{9})\s*⑆", RegexOptions.Compiled);

    private static readonly Regex E13bOnUsAccountRegex = new(@"⑈(\d{4,17})⑈", RegexOptions.Compiled);

    private static readonly Regex MicrCheckTrailingOnUsRegex = new(@"(\d{4,17})⑈", RegexOptions.Compiled);

    /// <summary>「州 + 5 位邮编 + 可选 ZIP+4」行；其中 5 位/后 4 位易被误当成支票号。</summary>
    private static readonly Regex UsStateZipLineRegex = new(
        @"(?i)\b[A-Z]{2}\s+(?<zip>\d{5})(?:-(?<zip4>\d{4}))?\b",
        RegexOptions.Compiled);

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
    private static readonly Regex AmountWordsLikeRegex = new(
        @"(?i)\b(?:dollars?|only|and|thousand|hundred|million|cents?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)\b",
        RegexOptions.Compiled);

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
        "ln", "lane", "way", "court", "ct", "suite", "ste", "apt", "unit", "p.o.", "po", "box",
        "hwy", "highway", "rte", "route"
    };

    /// <summary>磁墨条上方常见银行名/品牌单行（与左上 <see cref="CheckOcrParsingProfile.BankNamePriorRegion"/> 互补）。</summary>
    private static readonly NormRegion BankNameMicrAdjacentAuxRegion = new(0.0, 0.40, 1.0, 0.76);

    private static bool LooksLikeMicrInkLine(string text) =>
        text.Contains('⑆', StringComparison.Ordinal) || text.Contains('⑈', StringComparison.Ordinal);

    /// <summary>「⑈左段⑈ ⑆路由⑆ 右段⑈」：按位数将较短段视为支票号、较长段（通常 ≥8）视为账号。</summary>
    private static bool TryAssignMicrCheckVsAccountByLength(string leftDigits, string rightDigits, out string? checkDigits, out string? accountDigits)
    {
        checkDigits = null;
        accountDigits = null;
        var L = leftDigits.Length;
        var R = rightDigits.Length;
        // 账号常见 ≥10 位；6+9 等组合在多家银行为「账号片段 + 支票号」，不能单凭长短互换（会误判 Chase 等）
        if (Math.Max(L, R) < 10)
            return false;
        if (L <= 7 && R >= 8)
        {
            checkDigits = leftDigits;
            accountDigits = rightDigits;
            return true;
        }

        if (R <= 7 && L >= 8)
        {
            checkDigits = rightDigits;
            accountDigits = leftDigits;
            return true;
        }

        if (L >= 8 && R >= 8)
        {
            if (L > R)
            {
                accountDigits = leftDigits;
                checkDigits = rightDigits;
                return true;
            }

            if (R > L)
            {
                accountDigits = rightDigits;
                checkDigits = leftDigits;
                return true;
            }

            accountDigits = rightDigits;
            checkDigits = leftDigits;
            return true;
        }

        if (L >= 8 && R < 8)
        {
            accountDigits = leftDigits;
            checkDigits = rightDigits;
            return true;
        }

        if (R >= 8 && L < 8)
        {
            accountDigits = rightDigits;
            checkDigits = leftDigits;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 紧跟 <c>⑆9位路由⑆</c> 之后出现「可选账号左侧 ⑈ + 账号数字 + ⑈ + 支票号」时解析账号（Vision 常把磁墨切成两行且漏读账号左侧 ⑈）。
    /// </summary>
    private static bool TryParseAuxiliaryOnUsAfterTransit(string normalizedText, int transitMatchEndIndex, out string? accountDigits)
    {
        accountDigits = null;
        if ((uint)transitMatchEndIndex > (uint)normalizedText.Length)
            return false;
        var remainder = normalizedText[transitMatchEndIndex..];
        var m = Regex.Match(remainder, @"^\s*⑈?([0-9]{6,17})⑈([0-9]{3,8})\b");
        if (!m.Success)
            return false;
        return TryAssignMicrCheckVsAccountByLength(m.Groups[1].Value, m.Groups[2].Value, out _, out accountDigits)
            && !string.IsNullOrEmpty(accountDigits);
    }

    /// <summary>在已归一化文本中，按「⑈…⑈ ⑆9位路由⑆ …⑈」提取支票号（与账号）；路由须通过 ABA。</summary>
    private static bool TryClassifyOnUsDigitsAroundRouting(string text, string routing9, out string? checkDigits, out string? accountDigits)
    {
        checkDigits = null;
        accountDigits = null;
        if (routing9.Length != 9)
            return false;
        var m = Regex.Match(text, $@"⑈(\d{{4,17}})⑈\s*⑆\s*{Regex.Escape(routing9)}\s*⑆\s*(\d{{4,17}})⑈");
        if (!m.Success)
            return false;
        return TryAssignMicrCheckVsAccountByLength(m.Groups[1].Value, m.Groups[2].Value, out checkDigits, out accountDigits);
    }

    /// <summary>从 MICR 文本中解析「磁墨支票号」（不含印刷号通道）；命中 ⑆…⑆ 两侧 on-us 时优先于末段 ⑈ 启发式。</summary>
    private static string? TryExtractMicrCheckBracketedAroundTransit(string? micrText)
    {
        if (string.IsNullOrWhiteSpace(micrText))
            return null;
        var norm = NormalizeMicrLikeText(micrText);
        foreach (Match tm in E13bTransitRoutingRegex.Matches(norm))
        {
            var rt = tm.Groups[1].Value;
            if (!AbaRoutingNumberValidator.IsValid(rt))
                continue;
            if (TryClassifyOnUsDigitsAroundRouting(norm, rt, out var check, out _) && !string.IsNullOrEmpty(check))
                return check;
        }

        return null;
    }

    /// <summary>支票号数字在忽略前导 0 下是否同一串（如 002594 与 2594）。</summary>
    private static string CanonicalCheckDigitsKey(string digits)
    {
        var t = digits.TrimStart('0');
        return t.Length == 0 ? digits : t;
    }

    private static bool CanonicalCheckDigitsEqual(string a, string b) =>
        string.Equals(CanonicalCheckDigitsKey(a), CanonicalCheckDigitsKey(b), StringComparison.Ordinal);

    /// <summary>MICR 与印刷在数值上等价时优先较短展示形（印刷 2594 优于磁墨 002594）。</summary>
    private static string PreferShorterCanonicallyEqualCheckDigits(string a, string b) =>
        CanonicalCheckDigitsEqual(a, b)
            ? (a.Length <= b.Length ? a : b)
            : a;

    /// <summary>印刷号候选是否像「路由号去前导 0」误读（如 061000227 → 61000227）。</summary>
    private static bool LooksLikeMisreadRoutingDigits(string number, string micrCorpus)
    {
        if (number.Length is < 6 or > 8)
            return false;
        var norm = NormalizeMicrLikeText(micrCorpus);
        foreach (Match tm in E13bTransitRoutingRegex.Matches(norm))
        {
            var rt = tm.Groups[1].Value;
            if (rt.Length != 9 || !AbaRoutingNumberValidator.IsValid(rt))
                continue;
            if (string.Equals(number, rt, StringComparison.Ordinal))
                return true;
            var trimmed = rt.TrimStart('0');
            if (trimmed.Length > 0 && string.Equals(number, trimmed, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>从全文按行提取含 E13B 磁墨符号的行，供 <see cref="MicrLineRaw"/>（无磁墨行时返回 null）。</summary>
    public static string? TryBuildMicrLineRawFromPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && LooksLikeMicrInkLine(l))
            .ToList();
        if (lines.Count == 0)
            return null;
        var joined = string.Join("\n", lines);
        const int max = 640;
        return joined.Length <= max ? joined : joined[^max..];
    }

    /// <summary>从 Vision Read 行几何中选取磁墨行原文：自下而上，优先含解析出的 9 位路由。</summary>
    public static string? TryResolveMicrLineRawFromLayout(ReadOcrLayout layout, string? routingNumber)
    {
        if (layout.Lines.Count == 0)
            return null;
        var ink = layout.Lines
            .Where(l => LooksLikeMicrInkLine(l.Text))
            .OrderByDescending(l => l.NormCenterY)
            .ThenBy(l => l.NormLeft)
            .ToList();
        if (ink.Count == 0)
            return null;
        if (routingNumber is { Length: 9 } rt)
        {
            var filtered = ink.Where(l => l.Text.Contains(rt, StringComparison.Ordinal)).ToList();
            if (filtered.Count > 0)
                ink = filtered;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var line in ink)
        {
            var t = line.Text.Trim();
            if (t.Length == 0 || !seen.Add(t))
                continue;
            parts.Add(t);
        }

        if (parts.Count == 0)
            return null;
        var joined = string.Join("\n", parts);
        const int max = 640;
        return joined.Length <= max ? joined : joined[^max..];
    }

    /// <summary>MICR 区内若无 E13B/磁墨符号，仅数字滑动窗或 legacy 结果不可信（易把日期/金额拼成假 ABA），应回退全文。</summary>
    private static bool AcceptRegionMicrParse(string regionText, MicrHeuristicParseResult p)
    {
        if (p.RoutingNumber is not { Length: 9 } || p.RoutingAbaChecksumValid != true)
            return false;
        if (p.RoutingSelectionMode.StartsWith("e13b_transit", StringComparison.Ordinal)
            || string.Equals(p.RoutingSelectionMode, "triple_line", StringComparison.Ordinal))
            return true;
        return LooksLikeMicrInkLine(regionText);
    }

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
            if (AcceptRegionMicrParse(regionText, parsedRegion))
                return parsedRegion with { RoutingSelectionMode = $"{parsedRegion.RoutingSelectionMode}_region" };

            var bottomLineText = string.Join("\n", regionLines.TakeLast(Math.Min(4, regionLines.Count)).Select(line => line.Text));
            var parsedBottom = ParseMicrHeuristic(bottomLineText);
            if (AcceptRegionMicrParse(bottomLineText, parsedBottom))
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
        var fullNormMicr = NormalizeMicrLikeText(layout.FullText);
        var regionMicr = layout.ConcatLinesInRegion(profile.MicrPriorRegion, "\n").Trim();
        var inkFromPlain = TryBuildMicrLineRawFromPlainText(layout.FullText);
        var micrParts = new List<string>();
        if (regionMicr.Length > 0)
            micrParts.Add(regionMicr);
        if (!string.IsNullOrEmpty(inkFromPlain))
            micrParts.Add(inkFromPlain);
        var micrText = micrParts.Count > 0
            ? string.Join("\n", micrParts.Distinct(StringComparer.Ordinal))
            : string.Empty;
        if (string.IsNullOrWhiteSpace(micrText))
            micrText = TailFallback(layout.FullText, 600);
        else if (!LooksLikeMicrInkLine(micrText) && fullNormMicr.Contains('⑆', StringComparison.Ordinal))
            micrText = micrText + "\n" + layout.FullText;

        var printedRegion = profile.PrintedCheckPriorRegion;
        var printedText = layout.ConcatLinesInRegion(printedRegion, "\n");
        if (string.IsNullOrWhiteSpace(printedText))
            printedText = layout.FullText;

        var micrNorm = NormalizeMicrLikeText(micrText);
        var micrBracketedCheck = TryExtractMicrCheckBracketedAroundTransit(micrNorm)
            ?? TryExtractMicrCheckBracketedAroundTransit(fullNormMicr);
        var micrNumber = micrBracketedCheck;
        if (micrNumber is null)
        {
            var micrMatch = MicrCheckNumberRegex.Match(micrNorm);
            micrNumber = micrMatch.Success ? micrMatch.Groups[1].Value.Trim() : null;
        }

        if (micrNumber is null)
        {
            var onUs = MicrCheckTrailingOnUsRegex.Matches(micrNorm);
            if (onUs.Count > 0)
                micrNumber = onUs[^1].Groups[1].Value.Trim();
        }

        var printedCandidate = PickBestPrintedCheckCandidate(layout, profile, printedText, micrNumber);
        var printedNumber = printedCandidate.number;
        var printedScore = printedCandidate.score;

        if (micrNumber != null && printedNumber != null)
        {
            if (micrNumber == printedNumber || CanonicalCheckDigitsEqual(micrNumber, printedNumber))
                return (PreferShorterCanonicallyEqualCheckDigits(micrNumber, printedNumber), 0.94);
            if (micrBracketedCheck != null)
                return (micrNumber, 0.82);
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
        var norm = NormalizeMicrLikeText(text);
        var micrNumber = TryExtractMicrCheckBracketedAroundTransit(norm);
        if (micrNumber is null)
        {
            var micrMatch = MicrCheckNumberRegex.Match(norm);
            if (micrMatch.Success)
                micrNumber = micrMatch.Groups[1].Value.Trim();
        }

        if (micrNumber is null)
        {
            var onUs = MicrCheckTrailingOnUsRegex.Matches(norm);
            if (onUs.Count > 0)
                micrNumber = onUs[^1].Groups[1].Value.Trim();
        }

        var printedMatch = PrintedCheckNumberRegex.Match(text);
        string? printedNumber = printedMatch.Success ? printedMatch.Groups[1].Value.Trim() : null;
        if (printedNumber is not null && LooksLikeMisreadRoutingDigits(printedNumber, norm))
            printedNumber = null;
        if (printedNumber is not null && printedNumber.Length == 5
            && Regex.IsMatch(text, $@"(?i)\b[A-Z]{{2}}\s+{Regex.Escape(printedNumber)}(?:-\d{{4}})?\b"))
            printedNumber = null;
        if (printedNumber is not null && printedNumber.Length == 4
            && Regex.IsMatch(text, $@"(?i)\b[A-Z]{{2}}\s+\d{{5}}-{Regex.Escape(printedNumber)}\b"))
            printedNumber = null;

        if (micrNumber != null && printedNumber != null)
        {
            if (micrNumber == printedNumber)
                return (micrNumber, 0.92);
            if (CanonicalCheckDigitsEqual(micrNumber, printedNumber))
                return (PreferShorterCanonicallyEqualCheckDigits(micrNumber, printedNumber), 0.92);
            return (micrNumber, 0.55);
        }

        if (micrNumber != null)
            return (micrNumber, 0.72);
        if (printedNumber != null)
            return (printedNumber, 0.62);

        return (string.Empty, 0.1);
    }

    private static (string? number, double score) PickBestPrintedCheckCandidate(
        ReadOcrLayout layout,
        CheckOcrParsingProfile profile,
        string printedText,
        string? micrCheckHint = null)
    {
        var candidates = new List<(string number, double score)>();
        var printedRegion = profile.PrintedCheckPriorRegion;
        var micrRegion = profile.MicrPriorRegion;
        var routingMisreadCorpus = NormalizeMicrLikeText(layout.FullText);

        foreach (var line in layout.Lines)
        {
            foreach (Match match in PrintedCheckNumberLineRegex.Matches(line.Text))
            {
                if (!match.Success)
                    continue;

                var number = match.Groups["num"].Value.Trim();
                if (number.Length is < 4 or > 8)
                    continue;

                if (UsStateZipLineRegex.Match(line.Text) is { Success: true } zipM)
                {
                    if (string.Equals(zipM.Groups["zip"].Value, number, StringComparison.Ordinal))
                        continue;
                    if (zipM.Groups["zip4"].Value is { Length: 4 } z4 && string.Equals(z4, number, StringComparison.Ordinal))
                        continue;
                }

                if (LooksLikeMisreadRoutingDigits(number, routingMisreadCorpus))
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
            if (micrCheckHint is { Length: > 0 } hint)
            {
                var aligned = candidates.Where(c => CanonicalCheckDigitsEqual(c.number, hint)).ToList();
                if (aligned.Count > 0)
                {
                    var bestAligned = aligned
                        .OrderBy(x => x.number.Length)
                        .ThenByDescending(x => x.score)
                        .First();
                    return (bestAligned.number, Math.Clamp(bestAligned.score + 0.08, 0.22, 0.92));
                }
            }

            var best = candidates
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.number.Length)
                .First();
            return (best.number, Math.Clamp(best.score, 0.20, 0.92));
        }

        var printedMatch = PrintedCheckNumberRegex.Match(printedText);
        if (printedMatch.Success)
        {
            var v = printedMatch.Groups[1].Value.Trim();
            if (LooksLikeMisreadRoutingDigits(v, routingMisreadCorpus))
                return (null, 0.1);
            if (v.Length == 5 && Regex.IsMatch(printedText, $@"(?i)\b[A-Z]{{2}}\s+{Regex.Escape(v)}(?:-\d{{4}})?\b"))
                return (null, 0.1);
            if (v.Length == 4 && Regex.IsMatch(printedText, $@"(?i)\b[A-Z]{{2}}\s+\d{{5}}-{Regex.Escape(v)}\b"))
                return (null, 0.1);
            return (v, 0.62);
        }

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

            if (!DateTime.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                continue;

            var score = 0.62;
            if (profile.DatePriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
                score += 0.18;
            if (line.NormCenterY < 0.45 && line.NormCenterX < 0.6)
                score += 0.06;
            if (Regex.IsMatch(line.Text, @"(?i)\bdate\b"))
                score += 0.2;
            if (Regex.IsMatch(line.Text, @"(?i)(?!date\b)[a-z]{4,30}\s+\d{1,2}/\d{1,2}/\d{2,4}\b"))
                score -= 0.42;

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

        if (DateTime.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
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

        // 0) E13B transit 符号明确包裹的路由号（避免全文拼接数字后多个 ABA 合法窗误选）
        Match? lastTransit = null;
        foreach (Match m in E13bTransitRoutingRegex.Matches(text))
        {
            if (AbaRoutingNumberValidator.IsValid(m.Groups[1].Value))
                lastTransit = m;
        }

        if (lastTransit is not null)
        {
            var rt = lastTransit.Groups[1].Value;
            string? account = null;
            if (TryClassifyOnUsDigitsAroundRouting(text, rt, out _, out var acBracket) && !string.IsNullOrEmpty(acBracket))
                account = acBracket;
            if (account is null)
            {
                foreach (Match am in E13bOnUsAccountRegex.Matches(text))
                {
                    var cand = am.Groups[1].Value;
                    if (cand.Length >= 8)
                    {
                        account = cand;
                        break;
                    }
                }
            }

            account ??= E13bOnUsAccountRegex.Match(text) is { Success: true } firstOnUs
                ? firstOnUs.Groups[1].Value
                : null;

            var selectionMode = "e13b_transit";
            if (account is null && TryParseAuxiliaryOnUsAfterTransit(text, lastTransit.Index + lastTransit.Length, out var acAux))
            {
                account = acAux;
                selectionMode = "e13b_transit_aux_on_us";
            }

            var micrRaw = TryBuildMicrLineRawFromPlainText(text);
            micrRaw ??= text.Length <= 160 ? text.Trim() : text[(text.Length - 160)..].Trim();
            var acConf = account != null
                ? (selectionMode == "e13b_transit_aux_on_us" ? 0.56 : 0.68)
                : 0.35;
            return new MicrHeuristicParseResult(rt, account, micrRaw, 0.9, acConf, 0.88, true, selectionMode);
        }

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
            static bool HasE13bTransitDelimiters(string src, string routing) =>
                Regex.IsMatch(src, $@"⑆\s*{Regex.Escape(routing)}\s*⑆");

            var explicitMarked = abaWindows.Where(w => HasE13bTransitDelimiters(text, w.Routing)).ToList();
            var best = explicitMarked.Count > 0
                ? explicitMarked.MaxBy(x => x.Start)
                : abaWindows.MaxBy(x => x.Start);
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
        var primary = layout.Lines.Where(line =>
            profile.BankNamePriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true);
        var auxMicrAdjacent = layout.Lines.Where(line =>
            BankNameMicrAdjacentAuxRegion.Contains(line.NormCenterX, line.NormCenterY)
            && profile.BankNamePriorRegion?.Contains(line.NormCenterX, line.NormCenterY) != true
            && !LooksLikeMicrInkLine(line.Text)
            && line.Text.Trim().Length is >= 3 and <= 44
            && !Regex.IsMatch(line.Text.Trim(), @"(?i)^(pay\s+to|order\s+of|\$)"));

        var candidates = primary
            .Concat(auxMicrAdjacent)
            .Select(line => (line.Text, ScoreBankNameCandidate(line)))
            .Where(x => x.Item2 > 0.26)
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

    /// <summary>从版式「公司名优先带」抽取印刷商号/法人名（常见 INC./LLC/CORP 等后缀加权）。</summary>
    public static (string? companyName, double confidence) ParseCompanyName(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var region = profile.CompanyNamePriorRegion;
        if (region is null)
            return (null, 0.1);

        var candidates = RankCompanyNameCandidates(layout.Lines.Where(line => region.Contains(line.NormCenterX, line.NormCenterY)));
        // 模板/几何把抬头挤出默认带时，在上半张（避开 MICR）再扫一遍
        if (candidates.Count == 0)
        {
            candidates = RankCompanyNameCandidates(
                layout.Lines.Where(line =>
                    line.NormCenterY is >= 0.0 and <= 0.64
                    && line.NormCenterX is >= 0.0 and <= 0.995));
        }

        if (candidates.Count == 0)
            return (null, 0.1);

        var best = candidates[0];
        var bump = GetCorporateLegalSuffixBump(best.line.Text);
        var accepted = best.score >= 0.54
                       || (bump >= 0.44 && best.score >= 0.40)
                       || (bump is >= 0.18 and < 0.44 && best.score >= 0.52);

        if (!accepted)
            return (null, 0.1);

        return (NormalizeSpaces(best.line.Text), Math.Clamp(best.score, 0.22, 0.88));
    }

    /// <summary>prebuilt-check.us 的 PayerName 常与付款行/Bank 混淆，勿覆盖 Vision 解析出的持有人。</summary>
    internal static bool ShouldSkipDiPayerNameForAccountHolder(string? payerName, string? mergedBankName)
    {
        if (string.IsNullOrWhiteSpace(payerName))
            return false;
        var p = NormalizeSpaces(payerName.Trim());
        if (LooksLikeDraweeInstitutionBrandingLine(p))
            return true;
        if (!string.IsNullOrWhiteSpace(mergedBankName)
            && string.Equals(NormalizeSpaces(mergedBankName.Trim()), p, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static List<(ReadOcrLine line, double score)> RankCompanyNameCandidates(IEnumerable<ReadOcrLine> lines) =>
        lines
            .Select(line => (line, score: ScoreCompanyNameCandidate(line)))
            .Where(x => x.score > 0.30)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => GetCorporateLegalSuffixBump(x.line.Text))
            .ThenByDescending(x => x.line.Text.Length)
            .ThenBy(x => x.line.NormCenterY)
            .ToList();

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
            .Where(l => ShouldIncludeInAddressBlock(l.Text))
            .Select(l => l.Text)
            .Distinct()
            .Take(3)
            .ToList();
        var merged = NormalizeSpaces(string.Join(", ", block));
        return (merged, Math.Clamp(scored[0].Item2 + (block.Count > 1 ? 0.06 : 0.0), 0.22, 0.88));
    }

    private static bool ShouldIncludeInAddressBlock(string text)
    {
        var normalized = NormalizeSpaces(text);
        if (normalized.Length < 3)
            return false;
        if (LooksLikeAmountWordsOnly(normalized))
            return false;
        return true;
    }

    private static bool LooksLikeAmountWordsOnly(string text)
    {
        if (Regex.IsMatch(text, @"[$]|\d{1,3}(?:,\d{3})*(?:\.\d{2})?"))
            return false;

        var matches = AmountWordsLikeRegex.Matches(text);
        if (matches.Count < 2)
            return false;

        var lettersOnly = Regex.Replace(text, @"[^A-Za-z\s\-]", " ");
        var tokens = lettersOnly
            .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count == 0)
            return false;

        var amountWordTokens = tokens.Count(t => AmountWordsLikeRegex.IsMatch(t));
        return amountWordTokens >= Math.Max(2, (int)Math.Ceiling(tokens.Count * 0.6));
    }

    private static double ScoreBankNameCandidate(ReadOcrLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text))
            return 0.0;
        var normalized = NormalizeSpaces(line.Text);
        var t = normalized.Trim();
        if (Regex.IsMatch(t, @"^(?i)(pay(\s+to(\s+the)?)?|to\s+the|order\s+of|for|dollars|deposits|photo|mp)$"))
            return 0.0;
        var lower = normalized.ToLowerInvariant();
        if (!lower.Contains("bank", StringComparison.Ordinal)
            && Regex.IsMatch(t, @"(?i),\s*[A-Z]{2}\s+\d{5}(?:-\d{4})?\s*$"))
            return 0.0;

        // 门牌 + 街道词（如 15924 W HIGHWAY 40），勿当银行名
        if (Regex.IsMatch(t, @"^\d{1,6}\s", RegexOptions.None)
            && AddressStreetTokens.Any(token =>
                Regex.IsMatch(lower, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase)))
            return 0.0;

        if (!lower.Contains("bank", StringComparison.Ordinal) && AddressLineRegex.IsMatch(t))
            return 0.0;

        // 支票纸供应商（非付款行银行）
        if (Regex.IsMatch(lower, @"(?i)\bharland\s+clarke\b|\bdeluxe\b.*\bchecks?\b"))
            return 0.0;

        var normY = line.NormCenterY;
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

        // 磁墨上方短品牌名（REGIONS、CHASE 等），与左上地址/抬头竞争时提高权重
        if (normY >= 0.40 && normY <= 0.78)
        {
            score += 0.08;
            if (Regex.IsMatch(t, @"^(?i)[A-Za-z]{5,15}$"))
                score += 0.14;
            else if (Regex.IsMatch(t, @"^(?i)[A-Z0-9][A-Z0-9\s\.\&'\-]{2,26}$") && !Regex.IsMatch(t, @"\d{2,}"))
                score += 0.10;
        }

        var nameTokens = NameTokenRegex.Matches(t).Count;
        if (nameTokens >= 5 && !lower.Contains("bank", StringComparison.Ordinal) && !lower.Contains("credit union", StringComparison.Ordinal))
            score -= 0.16;
        if (normalized.Length > 38 && !lower.Contains("bank", StringComparison.Ordinal))
            score -= 0.08;

        return score;
    }

    /// <summary>美式商号/法人后缀强度：高档（INC/LLC/CORP…）与低档（GROUP/HOLDING…），用于公司名与持有人行加权。</summary>
    private static double GetCorporateLegalSuffixBump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0;
        var t = text.Trim();
        if (Regex.IsMatch(
                t,
                @"(?i)\b(inc\.?|llc\.?|l\.?\s*l\.?\s*c\.?|corp\.?|corporation|ltd\.?|limited|lp\b|pllc|p\.c\.)\b"))
            return 0.44;
        if (Regex.IsMatch(
                t,
                @"(?i)\b(company|co\.|holdings?|holding|enterprises?|enterprise|group|assoc\.?|association|intl\.?|international|partners?|trust|dba)\b"))
            return 0.18;
        return 0.0;
    }

    /// <summary>付款行/储蓄机构印刷品牌行（非 Pay to 抬头）。含高档法人后缀（如 Foo Bank LLC）则不排除。</summary>
    private static bool LooksLikeDraweeInstitutionBrandingLine(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;
        if (GetCorporateLegalSuffixBump(normalized) >= 0.44)
            return false;
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("credit union", StringComparison.Ordinal))
            return true;
        return lower.Contains("bank", StringComparison.Ordinal);
    }

    private static double ScoreCompanyNameCandidate(ReadOcrLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text))
            return 0.0;
        var t = NormalizeSpaces(line.Text);
        var lower = t.ToLowerInvariant();
        if (Regex.IsMatch(t, @"(?i)^(pay(\s+to(\s+the)?)?|to\s+the|order\s+of|for|memo)\b"))
            return 0.0;
        if (lower.Contains("pay to the order", StringComparison.Ordinal))
            return 0.0;
        if (LooksLikeMicrInkLine(t))
            return 0.0;

        if (LooksLikeDraweeInstitutionBrandingLine(t))
            return 0.0;

        var score = 0.28;
        score += GetCorporateLegalSuffixBump(t);

        var tokenCount = NameTokenRegex.Matches(t).Count;
        if (tokenCount >= 2)
            score += 0.10;
        if (tokenCount >= 3)
            score += 0.06;

        if (AddressLineRegex.IsMatch(t) && GetCorporateLegalSuffixBump(t) < 0.2)
            score -= 0.30;
        else if (Regex.IsMatch(t, @"^\d{1,6}\s", RegexOptions.None)
                 && AddressStreetTokens.Any(st =>
                     Regex.IsMatch(lower, $@"\b{Regex.Escape(st)}\b", RegexOptions.IgnoreCase)))
            score -= 0.22;

        if (Regex.IsMatch(t, @"\d{5,}"))
            score -= 0.12;

        var cy = line.NormCenterY;
        if (cy is >= 0.10 and <= 0.62)
            score += 0.06;
        if (t.Length is >= 4 and <= 72)
            score += 0.04;

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
        if (LooksLikeDraweeInstitutionBrandingLine(normalized))
            return 0.0;
        var tokenCount = NameTokenRegex.Matches(normalized).Count;
        if (tokenCount >= 2)
            score += 0.26;
        if (tokenCount >= 3)
            score += 0.08;
        score += GetCorporateLegalSuffixBump(normalized) * 0.42;
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
