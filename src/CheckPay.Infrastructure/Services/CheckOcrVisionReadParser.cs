using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Vision 将路由同行账号前缀与下行「719 1⑈」（折叠后为 7191⑈）拆开，支票序号再在下一行纯数字且无 ⑈ 时，
    /// <see cref="TryAssignMicrCheckVsAccountByLength"/> 无法生效；用于拼回账号并在下行取支票磁墨序号。
    /// </summary>
    private static readonly Regex TransitFragmentMicrSplitRegex = new(
        @"⑆\s*(?<rt>\d{9})\s*⑆\s*(?<p1>\d{2,8})\s*\r?\n\s*(?<mid>\d{4,11})\s*⑈(?:\s*\r?\n\s*(?<chk>\d{3,8})\s*)?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// <c>⑆ABA⑆ account⑈</c> 同行以单个 ⑈ 结束账号（≥8 位），其後 **换行或同行空格** 再接磁墨支票序（如下行 <c>02728</c>、同行 <c>01023</c>）；勿将账号误作 <see cref="MicrCheckTrailingOnUsRegex"/> 支票序。
    /// </summary>
    private static readonly Regex TransitClosedAccountThenDigitsCheckRegex = new(
        @"⑆\s*(?<rt>\d{9})\s*⑆\s*(?<acct>\d{8,17})⑈(?:\s*\r?\n\s*|\s+)(?<chk>\d{3,8})\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Navy Federal 等：<c>⑆ABA⑆</c> 后 **短支票序** + **⑉或⑈** + **长账号⑈**（如 <c>0511⑉7208453105⑈001</c>）；⑉ 常为 Read 替代 on-us 符。
    /// </summary>
    private static readonly Regex TransitShortCheckDelimiterLongAccountRegex = new(
        @"⑆\s*(?<rt>\d{9})\s*⑆\s*(?<chk>\d{3,8})(?:⑉|⑈)(?<acct>\d{8,17})⑈(?:\s*(?<trail>\d{1,8}))?\b",
        RegexOptions.Compiled);

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

    /// <summary>Vision 欧式小数：<c>$ 2046,39</c>（逗号为小数点）。</summary>
    private static readonly Regex AmountDollarCommaDecimalRegex = new(
        @"\$\s*(\d{1,9},\d{2})\b",
        RegexOptions.Compiled);

    /// <summary>票面印刷金额：<c>$ 686-25</c>（美元整数 + 连字符 + 两位分）。</summary>
    private static readonly Regex AmountDollarHyphenCentsRegex = new(
        @"\$\s*([\d,]{1,9})\s*[-–]\s*(\d{2})\b",
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

    /// <summary>票面银行法人后缀「, N.A.」（National Association），如 Wells Fargo、Chase Bank。</summary>
    private static readonly Regex BankNameNationalAssociationSuffixRegex = new(@"(?i),\s*N\.A\.?\s*$", RegexOptions.Compiled);

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

    /// <summary>磁墨条上方常见银行名/品牌单行（与左上 <see cref="CheckOcrParsingProfile.BankNamePriorRegion"/> 互补；minNormY 与 Prior max 对齐，避免 Chase 等品牌落在 0.28~0.40 断层）。</summary>
    /// <summary>付款行短品牌（REGIONS、TRUIST…）常印在磁墨条正上方，Read 的 normY 可 &gt;0.76；上限扩至近底以免漏检。</summary>
    private static readonly NormRegion BankNameMicrAdjacentAuxRegion = new(0.0, 0.28, 1.0, 0.94);

    private static bool LooksLikeMicrInkLine(string text) =>
        text.Contains('⑆', StringComparison.Ordinal) || text.Contains('⑈', StringComparison.Ordinal)
                                                   || text.Contains('⑉', StringComparison.Ordinal);

    /// <summary>
    /// Vision 常在同一磁墨行内把账号数字断开空格；仅在含 ⑆/⑈/⑉ 的行内折叠「数字↔数字」间空白，避免破坏正文里的三连 MICR 数字行。
    /// </summary>
    private static string CollapseSpacesBetweenDigitsOnMicrInkLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            var line = lines[i];
            sb.Append(LooksLikeMicrInkLine(line)
                ? Regex.Replace(line, @"(?<=\d)\s+(?=\d)", "")
                : line);
        }

        return sb.ToString();
    }

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

    /// <summary>
    /// <c>⑆路由⑆</c> 之后下一片段为「≥7 位数字 + ⑈」（常与左侧 ⑈…⑈ 同行断开），在双侧 bracket 分类失败且无非辅助命中时用尾段作账号。
    /// </summary>
    private static bool TryPickMicrAccountAfterTransitTail(string normalizedText, Match lastTransitMatch, out string? accountDigits)
    {
        accountDigits = null;
        var end = lastTransitMatch.Index + lastTransitMatch.Length;
        if ((uint)end > (uint)normalizedText.Length)
            return false;
        var tail = normalizedText[end..];
        var m = Regex.Match(tail, @"(\d{7,17})⑈");
        if (!m.Success)
            return false;
        accountDigits = m.Groups[1].Value;
        return true;
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

    /// <summary>
    /// <c>⑈…⑈ ⑆路由⑆ …⑈</c> 已匹配但 <see cref="TryAssignMicrCheckVsAccountByLength"/> 因 max&lt;10 放弃时，用位数启发式**仅**解析磁墨支票号
    /// （不充当账号分类，避免影响 <see cref="ParseMicrHeuristic"/>）：max≤7 较短段；路由前短 + 路由后长（Peoples 断行账号）取左；对称取右；其余再按较长段。
    /// </summary>
    private static bool TryPickBracketedMicrCheckShortPairFallback(string normalizedText, string routing9, out string? checkDigits)
    {
        checkDigits = null;
        if (routing9.Length != 9 || !AbaRoutingNumberValidator.IsValid(routing9))
            return false;

        var m = Regex.Match(normalizedText, $@"⑈(\d{{4,17}})⑈\s*⑆\s*{Regex.Escape(routing9)}\s*⑆\s*(\d{{4,17}})⑈");
        if (!m.Success)
            return false;

        var left = m.Groups[1].Value;
        var right = m.Groups[2].Value;
        if (TryAssignMicrCheckVsAccountByLength(left, right, out _, out _))
            return false;

        var L = left.Length;
        var R = right.Length;
        var mx = Math.Max(L, R);
        if (mx <= 7)
            checkDigits = L <= R ? left : right;
        else if (mx >= 8)
        {
            if (L <= 7 && R >= 8)
            {
                // 同行「短⑈ ⑆路由⑆ 长⑈」时 Peoples 取左；若路由闭合与右段之间**换行**且右段 ≥9 位（常为独立磁墨支票号），取右。
                // 右段 8 位且换行时多为 Peoples 换行长账号，仍取左（见单测 Peoples vs SkipsZip）。
                if (right.Length >= 9
                    && NewlineBetweenTransitCloseAndRightDigits(normalizedText, routing9, right))
                    checkDigits = right;
                else
                    checkDigits = left;
            }
            else if (R <= 7 && L >= 8)
                checkDigits = right;
            else
                checkDigits = L > R ? left : right;
        }
        else
            return false;

        return !string.IsNullOrEmpty(checkDigits);
    }

    /// <summary>
    /// 与 <see cref="TryPickBracketedMicrCheckShortPairFallback"/> 配合：仅当右侧段 <paramref name="rightDigits"/> 长度 ≥9 时才可能覆盖 Peoples「左短右长换行=账号」的默认。
    /// </summary>
    private static bool NewlineBetweenTransitCloseAndRightDigits(string normalizedText, string routing9, string rightDigits)
    {
        if (routing9.Length != 9 || rightDigits.Length == 0)
            return false;
        var close = $"⑆{routing9}⑆";
        for (var i = 0; ;)
        {
            var idx = normalizedText.IndexOf(close, i, StringComparison.Ordinal);
            if (idx < 0)
                return false;
            var afterClose = normalizedText[(idx + close.Length)..];
            if (afterClose.StartsWith(rightDigits + "⑈", StringComparison.Ordinal))
                return false;
            var nlAt = afterClose.IndexOfAny(['\r', '\n']);
            if (nlAt >= 0)
            {
                var afterNl = afterClose[(nlAt + 1)..].TrimStart('\r', '\n', ' ', '\t');
                if (afterNl.StartsWith(rightDigits + "⑈", StringComparison.Ordinal))
                    return true;
            }

            i = idx + 1;
        }
    }

    /// <summary>
    /// 路由 <c>⑆ABA⑆</c> 后账号被切成「同行前缀 + 下行 mid⑈」，支票序号为再下行纯数字（常无 ⑈）时的合并提取。
    /// </summary>
    private static bool TryRecoverTransitFragmentMicrSplit(string micrCorpus, out string? accountDigits, out string? checkDigits)
    {
        accountDigits = null;
        checkDigits = null;
        if (string.IsNullOrWhiteSpace(micrCorpus))
            return false;

        var t = NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(micrCorpus.Trim()));
        var m = TransitFragmentMicrSplitRegex.Match(t);
        if (!m.Success)
            return false;

        var rt = m.Groups["rt"].Value;
        if (!AbaRoutingNumberValidator.IsValid(rt))
            return false;

        var p1 = m.Groups["p1"].Value;
        var mid = m.Groups["mid"].Value;
        var merged = string.Concat(p1, mid);
        if (merged.Length < 8)
            return false;

        accountDigits = merged;
        if (m.Groups["chk"].Success)
            checkDigits = m.Groups["chk"].Value.Trim();

        return true;
    }

    /// <summary>
    /// <c>⑆ABA⑆</c> 后「长账号 + ⑈」再 **换行或同行空格** 接磁墨支票序（FIRST BANK 断行 / Wells Fargo 同行 <c>01023</c>）。
    /// </summary>
    private static bool TryRecoverMicrCheckTransitClosedAccountFollowedByDigits(string micrCorpus, out string? checkDigits)
    {
        checkDigits = null;
        if (string.IsNullOrWhiteSpace(micrCorpus))
            return false;

        var t = NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(micrCorpus.Trim()));
        var m = TransitClosedAccountThenDigitsCheckRegex.Match(t);
        if (!m.Success)
            return false;

        if (!AbaRoutingNumberValidator.IsValid(m.Groups["rt"].Value))
            return false;

        var acct = m.Groups["acct"].Value;
        var chk = m.Groups["chk"].Value.Trim();
        if (chk.Length is < 3 or > 8)
            return false;

        if (string.Equals(chk, acct, StringComparison.Ordinal))
            return false;
        // 避免误把账号后缀当成支票序
        if (chk.Length >= 4 && acct.EndsWith(chk, StringComparison.Ordinal))
            return false;

        checkDigits = chk;
        return true;
    }

    /// <summary>
    /// Navy Federal 等：<c>⑆ABA⑆</c> + 短支票序 + <c>⑉</c> 或 <c>⑈</c> + 长账号 + <c>⑈</c>（如 <c>0511⑉7208453105⑈001</c>），避免将长账号尾段误作磁墨支票号。
    /// </summary>
    private static bool TryRecoverMicrCheckTransitShortCheckThenLongAccount(string micrCorpus, out string? checkDigits)
    {
        checkDigits = null;
        if (string.IsNullOrWhiteSpace(micrCorpus))
            return false;

        var t = NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(micrCorpus.Trim()));
        var m = TransitShortCheckDelimiterLongAccountRegex.Match(t);
        if (!m.Success)
            return false;

        if (!AbaRoutingNumberValidator.IsValid(m.Groups["rt"].Value))
            return false;

        var acct = m.Groups["acct"].Value;
        var chk = m.Groups["chk"].Value.Trim();
        if (chk.Length is < 3 or > 8 || acct.Length < 8)
            return false;

        if (string.Equals(chk, acct, StringComparison.Ordinal))
            return false;
        if (acct.StartsWith(chk, StringComparison.Ordinal))
            return false;
        if (chk.Length >= 4 && acct.EndsWith(chk, StringComparison.Ordinal))
            return false;

        checkDigits = chk;
        return true;
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
            if (TryPickBracketedMicrCheckShortPairFallback(norm, rt, out var fbCheck) && !string.IsNullOrEmpty(fbCheck))
                return fbCheck;
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

    /// <summary>
    /// Vision 将 MICR 切成多行时，含路由的子串过滤会丢掉「仅有 ⑈ 尾段、不含 9 位 ABA」的相邻行；按垂直距离把同属一条磁墨带的行并入。
    /// </summary>
    private static List<ReadOcrLine> ExpandMicrInkLinesByVerticalProximity(
        IReadOnlyList<ReadOcrLine> allInk,
        IReadOnlyList<ReadOcrLine> seedLines,
        double maxNormCenterYDelta)
    {
        var chosen = new HashSet<ReadOcrLine>();
        foreach (var s in seedLines)
            chosen.Add(s);

        foreach (var line in allInk)
        {
            if (chosen.Contains(line))
                continue;
            var cy = line.NormCenterY;
            if (!seedLines.Any(s => Math.Abs(s.NormCenterY - cy) <= maxNormCenterYDelta))
                continue;
            chosen.Add(line);
        }

        return chosen
            .OrderByDescending(l => l.NormCenterY)
            .ThenBy(l => l.NormLeft)
            .ToList();
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
                ink = ExpandMicrInkLinesByVerticalProximity(ink, filtered, maxNormCenterYDelta: 0.14);
        }

        var chosen = new HashSet<ReadOcrLine>(ink);
        if (routingNumber is { Length: 9 })
        {
            foreach (var line in layout.Lines)
            {
                if (chosen.Contains(line))
                    continue;
                var t = line.Text.Trim();
                if (!Regex.IsMatch(t, @"^\d{3,8}$"))
                    continue;
                var cy = line.NormCenterY;
                if (!chosen.Any(s => Math.Abs(s.NormCenterY - cy) <= 0.16))
                    continue;
                chosen.Add(line);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var line in chosen.OrderBy(l => l.NormCenterY).ThenBy(l => l.NormLeft))
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

        var micrNorm = NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(micrText));
        _ = TryRecoverTransitFragmentMicrSplit(micrText, out var fragAcctDigits, out var fragMicrCheckDigits);

        var micrBracketedCheck = TryExtractMicrCheckBracketedAroundTransit(micrNorm)
            ?? TryExtractMicrCheckBracketedAroundTransit(fullNormMicr);
        string? micrNumber = micrBracketedCheck;
        if (micrNumber is null && !string.IsNullOrEmpty(fragMicrCheckDigits))
            micrNumber = fragMicrCheckDigits;

        var micrFromNavyShortDelimiterAccount = false;
        if (micrNumber is null)
        {
            string? navyChk = null;
            if (!TryRecoverMicrCheckTransitShortCheckThenLongAccount(micrNorm, out navyChk))
                TryRecoverMicrCheckTransitShortCheckThenLongAccount(
                    NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(layout.FullText)),
                    out navyChk);
            if (!string.IsNullOrEmpty(navyChk))
            {
                micrNumber = navyChk;
                micrFromNavyShortDelimiterAccount = true;
            }
        }

        if (micrNumber is null)
        {
            string? closedChk = null;
            if (!TryRecoverMicrCheckTransitClosedAccountFollowedByDigits(micrNorm, out closedChk))
                TryRecoverMicrCheckTransitClosedAccountFollowedByDigits(
                    NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(layout.FullText)),
                    out closedChk);
            if (!string.IsNullOrEmpty(closedChk))
                micrNumber = closedChk;
        }

        if (micrNumber is null)
        {
            var micrMatch = MicrCheckNumberRegex.Match(micrNorm);
            if (micrMatch.Success)
                micrNumber = micrMatch.Groups[1].Value.Trim();
        }

        if (!string.IsNullOrEmpty(fragAcctDigits) && micrNumber != null && micrBracketedCheck is null
                                                      && string.IsNullOrEmpty(fragMicrCheckDigits)
                                                      && fragAcctDigits.StartsWith(micrNumber, StringComparison.Ordinal)
                                                      && fragAcctDigits.Length >= 8 && micrNumber.Length <= 7)
            micrNumber = null;

        if (micrNumber is null)
        {
            // 碎片账号合并命中且无磁墨支票行时，`(\d+)⑈` 多为下行账号 mid（如 7191⑈），勿当作支票序号。
            var skipTrailingOnUsAsMicrCheck = !string.IsNullOrEmpty(fragAcctDigits)
                && string.IsNullOrEmpty(fragMicrCheckDigits)
                && micrBracketedCheck is null;
            if (!skipTrailingOnUsAsMicrCheck)
            {
                var onUs = MicrCheckTrailingOnUsRegex.Matches(micrNorm);
                if (onUs.Count > 0)
                    micrNumber = onUs[^1].Groups[1].Value.Trim();
            }
        }

        var printedMicrHint = micrNumber;
        if (printedMicrHint is null && !string.IsNullOrEmpty(fragMicrCheckDigits))
            printedMicrHint = fragMicrCheckDigits;

        var printedCandidate = PickBestPrintedCheckCandidate(layout, profile, printedText, printedMicrHint);
        var printedNumber = printedCandidate.number;
        var printedScore = printedCandidate.score;

        if (micrNumber != null && printedNumber != null)
        {
            if (micrNumber == printedNumber || CanonicalCheckDigitsEqual(micrNumber, printedNumber))
                return (PreferShorterCanonicallyEqualCheckDigits(micrNumber, printedNumber), 0.94);
            if (micrBracketedCheck != null || !string.IsNullOrEmpty(fragMicrCheckDigits) || micrFromNavyShortDelimiterAccount)
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
        var norm = NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(text));
        _ = TryRecoverTransitFragmentMicrSplit(text, out var fragAcctFt, out var fragChkFt);

        var micrBracketedFt = TryExtractMicrCheckBracketedAroundTransit(norm);
        var micrNumber = micrBracketedFt;
        if (micrNumber is null && !string.IsNullOrEmpty(fragChkFt))
            micrNumber = fragChkFt;

        if (micrNumber is null)
        {
            string? navyChkFt = null;
            if (!TryRecoverMicrCheckTransitShortCheckThenLongAccount(norm, out navyChkFt))
                TryRecoverMicrCheckTransitShortCheckThenLongAccount(
                    NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(text)),
                    out navyChkFt);
            if (!string.IsNullOrEmpty(navyChkFt))
                micrNumber = navyChkFt;
        }

        if (micrNumber is null)
        {
            string? closedChkFt = null;
            if (!TryRecoverMicrCheckTransitClosedAccountFollowedByDigits(norm, out closedChkFt))
                TryRecoverMicrCheckTransitClosedAccountFollowedByDigits(
                    NormalizeMicrLikeText(CollapseSpacesBetweenDigitsOnMicrInkLines(text)),
                    out closedChkFt);
            if (!string.IsNullOrEmpty(closedChkFt))
                micrNumber = closedChkFt;
        }

        if (micrNumber is null)
        {
            var micrMatch = MicrCheckNumberRegex.Match(norm);
            if (micrMatch.Success)
                micrNumber = micrMatch.Groups[1].Value.Trim();
        }

        if (!string.IsNullOrEmpty(fragAcctFt) && micrNumber != null && string.IsNullOrEmpty(fragChkFt)
                                                               && fragAcctFt.StartsWith(micrNumber, StringComparison.Ordinal)
                                                               && fragAcctFt.Length >= 8 && micrNumber.Length <= 7)
            micrNumber = null;

        if (micrNumber is null)
        {
            var skipTrailingOnUsAsMicrCheck = !string.IsNullOrEmpty(fragAcctFt)
                && string.IsNullOrEmpty(fragChkFt)
                && micrBracketedFt is null;
            if (!skipTrailingOnUsAsMicrCheck)
            {
                var onUs = MicrCheckTrailingOnUsRegex.Matches(norm);
                if (onUs.Count > 0)
                    micrNumber = onUs[^1].Groups[1].Value.Trim();
            }
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
            var lineTrim = line.Text.Trim();
            if (LooksLikeMicrInkLine(lineTrim))
                continue;
            if (LooksLikeInvoiceReferenceOrBankingMemoLine(lineTrim))
                continue;

            if (Regex.IsMatch(lineTrim, @"^\d{3}$"))
            {
                var number = lineTrim;
                var reject = false;
                if (UsStateZipLineRegex.Match(line.Text) is { Success: true } zipM)
                {
                    if (string.Equals(zipM.Groups["zip"].Value, number, StringComparison.Ordinal))
                        reject = true;
                    if (!reject && zipM.Groups["zip4"].Value is { Length: 4 } z4
                               && string.Equals(z4, number, StringComparison.Ordinal))
                        reject = true;
                }

                if (!reject && LooksLikeMisreadRoutingDigits(number, routingMisreadCorpus))
                    reject = true;

                if (!reject)
                {
                    var score = 0.26;
                    if (printedRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
                        score += 0.22;
                    if (line.NormCenterX >= 0.70)
                        score += 0.14;
                    if (line.NormCenterY <= 0.35)
                        score += 0.18;
                    if (Regex.IsMatch(line.Text, @"(?i)\bcheck\s*(?:no\.?|number|#)\b"))
                        score += 0.14;
                    if (Regex.IsMatch(line.Text, @"[$]|(?:\d{1,3},)?\d+\.\d{2}"))
                        score -= 0.26;
                    if (DateRegex.IsMatch(line.Text))
                        score -= 0.22;
                    if (micrRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
                        score -= 0.18;

                    candidates.Add((number, score));
                }
            }

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
            foreach (Match hm in AmountDollarHyphenCentsRegex.Matches(line.Text))
            {
                var dollars = hm.Groups[1].Value.Replace(",", "");
                var centsPart = hm.Groups[2].Value;
                if (!decimal.TryParse(dollars, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dInt))
                    continue;
                if (!int.TryParse(centsPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents2)
                    || cents2 is < 0 or > 99)
                    continue;
                var v = dInt + cents2 / 100m;
                if (v <= 0m)
                    continue;
                var score = ScoreAmountCandidate(line, hasDollar: true, profile, extraBoost: 0.22);
                scored.Add((v, score));
            }

            foreach (Match m in AmountRegex.Matches(line.Text))
            {
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace(",", "");
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0m)
                    continue;

                var hasDollar = m.Groups[1].Success;
                var score = ScoreAmountCandidate(line, hasDollar, profile);
                scored.Add((v, score));
            }

            foreach (Match cm in AmountDollarCommaDecimalRegex.Matches(line.Text))
            {
                var raw = cm.Groups[1].Value.Replace(",", ".", StringComparison.Ordinal);
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0m)
                    continue;
                var score = ScoreAmountCandidate(line, hasDollar: true, profile, extraBoost: 0.06);
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

    private static double ScoreAmountCandidate(ReadOcrLine line, bool hasDollar, CheckOcrParsingProfile profile, double extraBoost = 0)
    {
        var s = 0.52 + extraBoost;
        if (hasDollar)
            s += 0.12;
        if (profile.AmountPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            s += 0.16;
        if (line.NormCenterY < 0.55)
            s += 0.06;
        // 印刷「付给抬头」旁金额常在左半幅，未必落入默认 AmountPriorRegion（右半）
        if (hasDollar && line.NormCenterY < 0.42 && line.NormCenterX < 0.78)
            s += 0.10;
        if (!hasDollar && Regex.IsMatch(line.Text.Trim(), @"^(?:\d{1,4},)?\d+\.\d{2}$")
                        && line.NormCenterY < 0.14)
            s -= 0.20;
        if (profile.MicrPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            s -= 0.22;
        return s;
    }

    private static (decimal amount, double confidence) ParseAmountFullText(string text)
    {
        var hyphen = AmountDollarHyphenCentsRegex.Match(text);
        if (hyphen.Success
            && decimal.TryParse(hyphen.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hd)
            && int.TryParse(hyphen.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hc)
            && hc is >= 0 and <= 99)
        {
            var hv = hd + hc / 100m;
            if (hv > 0m)
                return (hv, 0.70);
        }

        var commaDec = AmountDollarCommaDecimalRegex.Match(text);
        if (commaDec.Success)
        {
            var cvRaw = commaDec.Groups[1].Value.Replace(",", ".", StringComparison.Ordinal);
            if (decimal.TryParse(cvRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cv) && cv > 0m)
                return (cv, 0.84);
        }

        var matches = AmountRegex.Matches(text);
        if (matches.Count == 0)
            return (0m, 0.1);

        var amounts = matches
            .Select(m =>
            {
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .Replace(",", "");
                return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
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
        var lines = layout.Lines;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
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

            if (AdjacentStandaloneDateLabelLine(lines, i))
                score += 0.36;

            if (LooksLikeHyphenPrintedDateSandwichedBetweenAddress(lines, i, m.Value))
                score -= 0.52;

            if (m.Value.Contains('/') && line.NormCenterY is >= 0.22 and <= 0.58
                                       && line.NormCenterX is >= 0.18 and <= 0.92)
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

    /// <summary>上一行或下一行为独立「DATE」标签（OCR 常把手写日与标签拆开）。</summary>
    private static bool AdjacentStandaloneDateLabelLine(IReadOnlyList<ReadOcrLine> lines, int dateLineIndex)
    {
        static bool IsLabel(string text) => Regex.IsMatch(text.Trim(), @"^(?i)date\s*$");
        return (dateLineIndex > 0 && IsLabel(lines[dateLineIndex - 1].Text))
               || (dateLineIndex + 1 < lines.Count && IsLabel(lines[dateLineIndex + 1].Text));
    }

    /// <summary>门牌街道行 + <c>MM-DD-YY</c> 印刷日 + 城市州邮编行（非支票落款日）。</summary>
    private static bool LooksLikeHyphenPrintedDateSandwichedBetweenAddress(
        IReadOnlyList<ReadOcrLine> lines,
        int dateLineIndex,
        string dateMatchValue)
    {
        if (!Regex.IsMatch(dateMatchValue, @"^\d{2}-\d{2}-\d{2}$"))
            return false;
        if (dateLineIndex <= 0 || dateLineIndex + 1 >= lines.Count)
            return false;
        var prev = lines[dateLineIndex - 1].Text.Trim();
        var next = lines[dateLineIndex + 1].Text.Trim();
        return AddressLineRegex.IsMatch(prev) && UsStateZipLineRegex.Match(next).Success;
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

        text = CollapseSpacesBetweenDigitsOnMicrInkLines(text.Trim());
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
            var bracketClassified =
                TryClassifyOnUsDigitsAroundRouting(text, rt, out _, out var acBracket) && !string.IsNullOrEmpty(acBracket);
            string? account = bracketClassified ? acBracket : null;
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
            var auxiliaryMatched = false;
            if (account is null && TryParseAuxiliaryOnUsAfterTransit(text, lastTransit.Index + lastTransit.Length, out var acAux))
            {
                account = acAux;
                selectionMode = "e13b_transit_aux_on_us";
                auxiliaryMatched = true;
            }

            var strongAccount = bracketClassified || auxiliaryMatched || (account?.Length ?? 0) >= 8;
            var transitTailRefinedAccount = false;
            if (!strongAccount && TryPickMicrAccountAfterTransitTail(text, lastTransit, out var tailAcct))
            {
                account = tailAcct;
                transitTailRefinedAccount = true;
            }

            var fragmentAccountMerge = false;
            if ((account?.Length ?? 0) < 8 && TryRecoverTransitFragmentMicrSplit(text, out var fragAcct, out _)
                                            && !string.IsNullOrEmpty(fragAcct))
            {
                account = fragAcct;
                fragmentAccountMerge = true;
                selectionMode = "e13b_transit_fragment_merge";
            }

            var micrRaw = TryBuildMicrLineRawFromPlainText(text);
            micrRaw ??= text.Length <= 160 ? text.Trim() : text[(text.Length - 160)..].Trim();
            var acConf = account != null
                ? fragmentAccountMerge ? 0.54
                : auxiliaryMatched ? 0.56
                : transitTailRefinedAccount ? 0.58
                : 0.68
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
        var (name, conf, _) = ResolveAcceptedCompanyNameLine(layout, profile);
        return (name, conf);
    }

    /// <summary>与 <see cref="ParseCompanyName"/> 相同判定，额外返回命中行供「商号下方印刷地址」锚定。</summary>
    internal static ReadOcrLine? TryGetAcceptedCompanyNameLine(ReadOcrLayout layout, CheckOcrParsingProfile profile) =>
        ResolveAcceptedCompanyNameLine(layout, profile).line;

    private static (string? name, double conf, ReadOcrLine? line) ResolveAcceptedCompanyNameLine(
        ReadOcrLayout layout,
        CheckOcrParsingProfile profile)
    {
        var region = profile.CompanyNamePriorRegion;
        if (region is null)
            return (null, 0.1, null);

        // 区域带 ∪ 上半张：避免票型 region 过窄或 Read 将抬头行 normY 标到偏下时漏选
        var inRegion = layout.Lines.Where(line => region.Contains(line.NormCenterX, line.NormCenterY));
        var upperBand = layout.Lines.Where(line =>
            line.NormCenterY is >= 0.0 and <= 0.64 && line.NormCenterX is >= 0.0 and <= 0.995);
        var fromGeom = TrySelectAcceptedCompanyNameWithLine(inRegion.Concat(upperBand).Distinct());
        if (fromGeom.name != null)
            return (fromGeom.name, fromGeom.conf, fromGeom.line);

        // 最后按 FullText 行序构造伪几何再跑同一套打分（与真实 bbox 解耦）
        if (!string.IsNullOrWhiteSpace(layout.FullText))
            return TrySelectAcceptedCompanyNameWithLine(BuildSyntheticCompanyLinesFromFullText(layout.FullText));

        return (null, 0.1, null);
    }

    private static (string? name, double conf, ReadOcrLine? line) TrySelectAcceptedCompanyNameWithLine(IEnumerable<ReadOcrLine> lines)
    {
        var candidates = RankCompanyNameCandidates(lines);
        if (candidates.Count == 0)
            return (null, 0.1, null);

        var best = candidates[0];
        var bump = GetCorporateLegalSuffixBump(best.line.Text);
        var accepted = best.score >= 0.54
                       || (bump >= 0.44 && best.score >= 0.40)
                       || (bump is >= 0.18 and < 0.44 && best.score >= 0.52);

        if (!accepted)
            return (null, 0.1, null);

        return (NormalizeSpaces(best.line.Text), Math.Clamp(best.score, 0.22, 0.88), best.line);
    }

    /// <summary>按 Read 拼接全文行序生成居中伪行，供公司名在几何失效时仍能从「1675 上一行 / INC 行」命中。</summary>
    private static IEnumerable<ReadOcrLine> BuildSyntheticCompanyLinesFromFullText(string fullText)
    {
        var segments = fullText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length >= 2)
            .Take(48)
            .ToList();
        var y = 0.09;
        foreach (var seg in segments)
        {
            yield return new ReadOcrLine(seg, 0.48, y, y - 0.009, y + 0.009, 0.10, 0.86);
            y += 0.017;
            if (y > 0.63)
                break;
        }
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

    private static bool LooksLikePayToOrderBandLine(string text)
    {
        var t = NormalizeSpaces(text);
        return Regex.IsMatch(t, @"(?i)^(pay(\s+to(\s+the)?)?|to\s+the|order\s+of)\b");
    }

    /// <summary>FOR INV、ACH RT 等备忘/对账行，勿进 account_address。</summary>
    private static bool LooksLikeInvoiceReferenceOrBankingMemoLine(string text)
    {
        var t = NormalizeSpaces(text);
        if (t.Length == 0)
            return false;
        if (Regex.IsMatch(t, @"(?i)\bfor\s+inv\b"))
            return true;
        if (Regex.IsMatch(t, @"(?i)\binv(?:oice)?\s*[#.]"))
            return true;
        if (Regex.IsMatch(t, @"(?i)\bach\s+rt\b"))
            return true;
        if (Regex.IsMatch(t, @"(?i)\bdeposit\s*!?\s*$"))
            return true;
        // 「FOR I 1234567」类备忘行上的长数字易被当成印刷支票号
        if (Regex.IsMatch(t, @"(?i)\bfor\s+i\s+\d{4,}"))
            return true;
        return false;
    }

    private static bool HasUsCityStateZipSignal(string text) =>
        Regex.IsMatch(NormalizeSpaces(text), @"\b[A-Z]{2}\s+\d{5}(?:-\d{4})?\b");

    private static bool HasStreetAddressSignal(string text)
    {
        var t = NormalizeSpaces(text);
        var lower = t.ToLowerInvariant();
        var hasStreetCue = AddressStreetTokens.Any(token =>
            Regex.IsMatch(lower, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase));
        return hasStreetCue && AddressLineRegex.IsMatch(t);
    }

    private static int AddressStreetOrZipRankHint(string text)
    {
        if (HasUsCityStateZipSignal(text))
            return 2;
        if (HasStreetAddressSignal(text))
            return 1;
        return 0;
    }

    /// <summary>
    /// 印刷商号行正下方、与商号同一左栏内的门牌+城市邮编（Read 常把街道行 normY 标到带外或与商号 X 偏差大）。
    /// 用「商号下窄纵向窗」排除中下部的 FOR INV / 付款人名 / 磁墨行。
    /// </summary>
    private static (string? address, double confidence) TryParseAccountAddressAnchoredBelowCompany(
        ReadOcrLayout layout,
        CheckOcrParsingProfile profile)
    {
        var companyLine = TryGetAcceptedCompanyNameLine(layout, profile);
        if (companyLine is null)
            return (null, 0.1);

        // FullText 伪几何行宽度假大：仍可用「左半幅 + 商号下窄纵带」取印刷地址
        var wideCompanyBox = companyLine.NormRight - companyLine.NormLeft > 0.58;

        // 仅处理票面上方印刷块（避免把中部 Pay to 附近误当地址）
        if (companyLine.NormCenterY > 0.44 || companyLine.NormCenterX > 0.68)
            return (null, 0.1);

        // 印刷地址几乎总在商号下很短一截内；过大则并到中下部 FOR INV / 付款行 / MICR
        var maxBandTop = Math.Min(companyLine.NormBottom + (wideCompanyBox ? 0.36 : 0.34), 0.52);

        bool RowInPrintedBand(ReadOcrLine l) =>
            l.NormTop >= companyLine.NormBottom - 0.03
            && l.NormTop <= maxBandTop;

        bool RowHorizontallyPlausible(ReadOcrLine l)
        {
            if (l.NormCenterX > 0.62)
                return false;
            if (wideCompanyBox)
                return l.NormCenterX <= 0.52;
            return Math.Abs(l.NormCenterX - companyLine.NormCenterX) <= 0.44
                   || (l.NormLeft <= companyLine.NormRight + 0.06 && l.NormRight >= companyLine.NormLeft - 0.06);
        }

        var ordered = layout.Lines
            .Where(RowInPrintedBand)
            .Where(RowHorizontallyPlausible)
            .OrderBy(l => l.NormTop)
            .ToList();

        var block = new List<ReadOcrLine>();
        foreach (var l in ordered)
        {
            if (ReferenceEquals(l, companyLine))
                continue;
            if (string.Equals(
                    NormalizeSpaces(l.Text),
                    NormalizeSpaces(companyLine.Text),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (LooksLikeMicrInkLine(l.Text))
                break;
            if (LooksLikePayToOrderBandLine(l.Text))
                break;
            if (LooksLikeInvoiceReferenceOrBankingMemoLine(l.Text))
                break;

            var t = NormalizeSpaces(l.Text);
            var sc = ScoreAddressCandidate(t);
            var isZipLine = HasUsCityStateZipSignal(t);
            var lower = t.ToLowerInvariant();
            var hasStreetCue = AddressStreetTokens.Any(token =>
                Regex.IsMatch(lower, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase));
            var doorPlate = AddressLineRegex.IsMatch(t);
            var plausibleStart = isZipLine
                                   || (doorPlate && hasStreetCue)
                                   || (doorPlate && Regex.IsMatch(t, @"^\d{1,6}\s"));

            if (sc <= 0.32 && !isZipLine && !(doorPlate && hasStreetCue))
            {
                if (block.Count > 0)
                    break;
                if (!plausibleStart)
                    continue;
            }

            if (!ShouldIncludeInAddressBlock(t))
                continue;

            block.Add(l);
            if (block.Count >= 4)
                break;
            if (isZipLine)
                break;
        }

        if (block.Count == 0)
            return (null, 0.1);

        // 至少一行像「门牌+街道」或「州 邮编」，避免并到无关备忘行后仍返回
        if (!block.Any(b => HasUsCityStateZipSignal(b.Text) || HasStreetAddressSignal(b.Text)))
            return (null, 0.1);

        var seedLine = block.MaxBy(x => ScoreAddressCandidate(x.Text))!;
        var seedScore = ScoreAddressCandidate(seedLine.Text);
        var merged = NormalizeSpaces(string.Join(", ", block.OrderBy(x => x.NormTop).Select(x => x.Text)));
        return (merged, Math.Clamp(seedScore + (block.Count > 1 ? 0.06 : 0.0), 0.22, 0.88));
    }

    public static (string? accountAddress, double confidence) ParseAccountAddress(ReadOcrLayout layout, CheckOcrParsingProfile profile)
    {
        var anchored = TryParseAccountAddressAnchoredBelowCompany(layout, profile);
        if (anchored.address != null)
            return (anchored.address, anchored.confidence);

        var regionLines = layout.Lines
            .Where(line => profile.AccountAddressPriorRegion?.Contains(line.NormCenterX, line.NormCenterY) == true)
            .Where(line => !LooksLikeMicrInkLine(line.Text))
            .Where(line => !LooksLikeInvoiceReferenceOrBankingMemoLine(line.Text))
            .OrderBy(line => line.NormTop)
            .ToList();
        if (regionLines.Count == 0)
            return (null, 0.1);

        var scoredLines = regionLines
            .Select(line => (line, score: ScoreAddressCandidate(line.Text)))
            .Where(x => x.score > 0.32)
            .ToList();
        if (scoredLines.Count == 0)
            return (null, 0.1);

        // 在「像地址」的行里优先高分种子，避免纯「州 邮编」行作种子后置信度被拉低、或与上门牌 Y 距偏大
        var hinted = scoredLines.Where(x => AddressStreetOrZipRankHint(x.line.Text) > 0).ToList();
        var seed = (hinted.Count > 0
                ? hinted
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.line.NormTop)
                : scoredLines.OrderByDescending(x => x.score))
            .First();

        const double yLink = 0.36;
        var pool = scoredLines.Select(x => x.line).ToList();
        var cluster = new List<ReadOcrLine> { seed.line };
        var queued = new HashSet<ReadOcrLine>();
        queued.Add(seed.line);
        var q = new Queue<ReadOcrLine>();
        q.Enqueue(seed.line);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            foreach (var other in pool)
            {
                if (queued.Contains(other))
                    continue;
                if (Math.Abs(other.NormCenterY - cur.NormCenterY) > yLink)
                    continue;
                queued.Add(other);
                cluster.Add(other);
                q.Enqueue(other);
            }
        }

        var block = cluster
            .Where(l => ShouldIncludeInAddressBlock(l.Text))
            .Distinct()
            .OrderBy(l => l.NormTop)
            .Take(4)
            .Select(l => l.Text)
            .ToList();
        if (block.Count == 0)
            return (null, 0.1);

        var merged = NormalizeSpaces(string.Join(", ", block));
        return (merged, Math.Clamp(seed.score + (block.Count > 1 ? 0.06 : 0.0), 0.22, 0.88));
    }

    private static bool ShouldIncludeInAddressBlock(string text)
    {
        var normalized = NormalizeSpaces(text);
        if (normalized.Length < 3)
            return false;
        if (LooksLikeMicrInkLine(normalized))
            return false;
        if (LooksLikeInvoiceReferenceOrBankingMemoLine(normalized))
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

    /// <summary>整行为票据日期或与 <see cref="DateRegex"/> 等效的独立日期行（含可选 DATE 前缀），勿当银行名。</summary>
    private static bool LooksLikeDateOnlyPrintedLine(string normalizedLine)
    {
        var t = NormalizeSpaces(normalizedLine).Trim();
        if (t.Length < 6)
            return false;

        var core = Regex.Replace(t, @"^(?i)date\s*[:\.]?\s*", "").Trim();
        if (core.Length < 6)
            return false;

        var m = DateRegex.Match(core);
        if (!m.Success || m.Index != 0)
            return false;

        return string.IsNullOrWhiteSpace(core.Substring(m.Length));
    }

    /// <summary>支票左上角常见的分数式 transit（如 Chase 样式的 9-32/720），勿当银行名称。</summary>
    private static bool LooksLikePrintedFractionalRoutingTransit(string t)
    {
        var s = NormalizeSpaces(t).Trim();
        return Regex.IsMatch(s, @"^\d{1,3}\s*-\s*\d{1,3}\s*/\s*\d{1,4}\s*$");
    }

    private static double ScoreBankNameCandidate(ReadOcrLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text))
            return 0.0;
        var normalized = NormalizeSpaces(line.Text);
        var t = normalized.Trim();
        if (LooksLikePrintedFractionalRoutingTransit(t))
            return 0.0;
        if (LooksLikeDateOnlyPrintedLine(t))
            return 0.0;
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
        if (BankNameNationalAssociationSuffixRegex.IsMatch(t))
            score += 0.38;
        if (BankNoiseTokens.Any(lower.Contains))
            score -= 0.2;
        if (normalized.Length is >= 6 and <= 48)
            score += 0.08;

        if (normY < 0.18)
            score += 0.06;

        // 付款行带上/近磁墨短品牌（REGIONS、TRUIST 等）与法人银行名加权；上沿与 <see cref="BankNameMicrAdjacentAuxRegion"/> 衔接
        if (normY >= 0.28 && normY <= 0.94)
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

        // 左上角易把付款商号（LLC/INC）当成银行：无法人银行线索时强降权
        var hasBankCue = lower.Contains("bank", StringComparison.Ordinal)
                         || lower.Contains("credit union", StringComparison.Ordinal)
                         || BankNameNationalAssociationSuffixRegex.IsMatch(t);
        if (!hasBankCue && GetCorporateLegalSuffixBump(t) >= 0.44)
            score -= 0.26;

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
                @"(?i)\b(inc\.?|llc\.?|lic\.?|l\.?\s*i\.?\s*c\.?|corp\.?|corporation|ltd\.?|limited|lp\b|pllc|p\.c\.)\b"))
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
        if (Regex.IsMatch(t, @"(?i)\$\s*[\d,]{1,9}\s*[-–]\s*\d{2}\b")
            || Regex.IsMatch(t, @"(?i)\$\s*[\d,]+\.\d{2}\b"))
            score -= 0.62;
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
        if (cy <= 0.12)
            score += 0.10;

        // 票面最上沿单行商号（如 remitter 餐馆名），常位于 Pay to 目录名之上且无 GROUP/INC
        if (cy <= 0.15
            && Regex.IsMatch(t, @"^(?i)[A-Za-z]{4,22}$")
            && !Regex.IsMatch(t, @"(?i)^(VOID|CHECK|PAY)$"))
            score += 0.30;

        // 中部「… Group / Holding …」多为收款方目录展示名，真正印刷商号常在页眉单行
        if (cy is >= 0.15 and <= 0.55
            && Regex.IsMatch(lower, @"(?i)\b(group|holdings?|holding)\b"))
            score -= 0.24;

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
        if (LooksLikeMicrInkLine(normalized))
            return 0.0;
        if (LooksLikeInvoiceReferenceOrBankingMemoLine(normalized))
            return 0.0;
        var lower = normalized.ToLowerInvariant();
        var score = 0.26;
        // 印刷商号（INC/LLC…）常命中门牌形 AddressLineRegex，勿当地址种子
        if (GetCorporateLegalSuffixBump(normalized) >= 0.44)
            score -= 0.62;
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
