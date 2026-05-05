using CheckPay.Infrastructure.Services;
using CheckPay.Application.Common.Models;

namespace CheckPay.Tests.Infrastructure;

public class CheckOcrRoutingMicrEuTests
{
    [Theory]
    [InlineData("021000021", true)]
    [InlineData("011000015", true)]
    [InlineData("123456789", false)]
    [InlineData("12345678", false)]
    public void AbaRoutingNumberValidator_ValidNineDigits(string digits, bool expected) =>
        Assert.Equal(expected, AbaRoutingNumberValidator.IsValid(digits));

    [Fact]
    public void ParseMicrHeuristic_TripleDigitLine_PrefersAbaRouting()
    {
        var text = "noise\n021000021 12345678901234567 4829\n";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("021000021", r.RoutingNumber);
        Assert.Equal("12345678901234567", r.AccountNumber);
        Assert.True(r.RoutingAbaChecksumValid);
        Assert.Equal("triple_line", r.RoutingSelectionMode);
    }

    [Fact]
    public void ParseMicrHeuristic_SlidingWindow_PicksRightmostValidAba()
    {
        // 尾部含非 ABA 的 9 位噪声 + 有效路由 + 长账号串
        var text = "ref 123456789 noise 021000021 12345678901234";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("021000021", r.RoutingNumber);
        Assert.Equal("aba_sliding_window", r.RoutingSelectionMode);
        Assert.True(r.RoutingAbaChecksumValid);
    }

    [Fact]
    public void ParseMicrHeuristic_NormalizesCommonOcrConfusableChars()
    {
        var text = "noise\nO21OO0021 12345678901234 4829\n";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("021000021", r.RoutingNumber);
        Assert.True(r.RoutingAbaChecksumValid);
    }

    /// <summary>路由单独成行，下一行「账号⑈支票号」且缺账号左侧 ⑈（Regions / Harland 类版式）。</summary>
    [Fact]
    public void ParseMicrHeuristic_E13bTransit_NewlineThenAuxiliaryOnUs_AccountAndMode()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var text = $"{transit}063104668{transit}\n0317832241{onUs}00764\nHarland Clarke";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("063104668", r.RoutingNumber);
        Assert.Equal("0317832241", r.AccountNumber);
        Assert.True(r.RoutingAbaChecksumValid);
        Assert.Equal("e13b_transit_aux_on_us", r.RoutingSelectionMode);
    }

    [Fact]
    public void ParseMicrHeuristic_E13bTransit_SameLineAuxiliaryOnUs_Account()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var text = $"{transit}063104668{transit}0317832241{onUs}00764";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("063104668", r.RoutingNumber);
        Assert.Equal("0317832241", r.AccountNumber);
        Assert.Equal("e13b_transit_aux_on_us", r.RoutingSelectionMode);
    }

    [Fact]
    public void ParseMicrHeuristic_WithLayout_PrefersMicrRegionOverFullTextNoise()
    {
        var lines = new[]
        {
            new ReadOcrLine("ref 123456789 noise", 0.22, 0.10, 0.08, 0.12, 0.05, 0.35),
            new ReadOcrLine("021000021 12345678901234567 4829", 0.52, 0.88, 0.86, 0.90, 0.10, 0.94)
        };
        var layout = new ReadOcrLayout("ref 123456789 noise\n021000021 12345678901234567 4829", lines, 1000, 1000);
        var result = CheckOcrVisionReadParser.ParseMicrHeuristic(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("021000021", result.RoutingNumber);
        Assert.Contains("region", result.RoutingSelectionMode, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMicrHeuristic_E13bTransit_ExtractsRoutingDespiteOtherValidAbaDigits()
    {
        // 全文中可出现多段 ABA 合法 9 位数字；E13B transit（U+2446）定界符内的路由应优先
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var text =
            """
            Bushido II LLC
            1408
            Summerville, SC 29485
            noise 140850055 noise
            """ + $"{onUs}001408{onUs} {transit}021000021{transit}\n562203631{onUs}\n";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("021000021", r.RoutingNumber);
        Assert.Equal("e13b_transit", r.RoutingSelectionMode);
        Assert.True(r.RoutingAbaChecksumValid);
    }

    [Fact]
    public void TryResolveMicrLineRawFromLayout_PrefersLineContainingRouting()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micr = $"{onUs}002594{onUs} {transit}061000227{transit} 2078288384{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("noise top", 0.5, 0.4, 0.39, 0.41, 0, 1),
            new ReadOcrLine($"{onUs}wrong{onUs}", 0.5, 0.5, 0.49, 0.51, 0, 1),
            new ReadOcrLine(micr, 0.5, 0.9, 0.89, 0.91, 0, 1)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);
        var raw = CheckOcrVisionReadParser.TryResolveMicrLineRawFromLayout(layout, "061000227");
        Assert.Equal(micr, raw);
    }

    [Fact]
    public void TryResolveMicrLineRawFromLayout_MergesAdjacentMicrRowMissingRoutingSubstring()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var row1 = $"{onUs}003358{onUs} {transit}061000227{transit}";
        var row2 = $"002 409 4{onUs}";
        var lines = new[]
        {
            new ReadOcrLine(row2, 0.52, 0.935, 0.925, 0.945, 0.08, 0.92),
            new ReadOcrLine(row1, 0.52, 0.905, 0.895, 0.915, 0.08, 0.92),
            new ReadOcrLine("noise", 0.5, 0.50, 0.49, 0.51, 0, 1)
        };
        var layout = new ReadOcrLayout(string.Join('\n', lines.Select(l => l.Text)), lines, 1000, 1000);
        var raw = CheckOcrVisionReadParser.TryResolveMicrLineRawFromLayout(layout, "061000227");
        Assert.NotNull(raw);
        Assert.Contains(row1, raw, StringComparison.Ordinal);
        Assert.True(raw.Replace(" ", "").Contains("0024094", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseMicrHeuristic_E13bTransit_TwoMicrRows_PrefersTransitTailSevenPlusDigitsOverShortBracket()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var text = $"{onUs}003358{onUs} {transit}061000227{transit}\n002 409 4{onUs}\n";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(text);
        Assert.Equal("061000227", r.RoutingNumber);
        Assert.Equal("0024094", r.AccountNumber);
        Assert.True(r.RoutingAbaChecksumValid);
        Assert.Equal("e13b_transit", r.RoutingSelectionMode);
    }

    [Fact]
    public void ParseMicrHeuristic_Layout_RejectsRegionSlidingWithoutMicrInk_FallsBackToFullTextE13b()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micr = $"{onUs}002594{onUs} {transit}061000227{transit} 2078288384{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("03/27/2026", 0.12, 0.74, 0.73, 0.75, 0.05, 0.25),
            new ReadOcrLine("$3442.80", 0.5, 0.78, 0.77, 0.79, 0.4, 0.6),
            new ReadOcrLine(micr, 0.5, 0.68, 0.67, 0.69, 0.1, 0.9)
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1000, 1000);
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("061000227", r.RoutingNumber);
        Assert.True(r.RoutingAbaChecksumValid);
        Assert.StartsWith("e13b_transit", r.RoutingSelectionMode, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCheckNumber_SkipsZipPlus4_BracketedShortIsCheckWhenAccountTenPlus()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micr = $"{onUs}002594{onUs} {transit}061000227{transit} 2078288384{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("Macon, GA 31201-1925", 0.22, 0.22, 0.20, 0.24, 0.05, 0.45),
            new ReadOcrLine(micr, 0.5, 0.88, 0.86, 0.90, 0.1, 0.9)
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1000, 1000);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("002594", cn);
        var micrR = CheckOcrVisionReadParser.ParseMicrHeuristic(micr);
        Assert.Equal("2078288384", micrR.AccountNumber);
    }

    [Fact]
    public void ParseCheckNumber_RejectsPrintedRoutingLeadingZeroTrimArtifact()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micr = $"{onUs}002594{onUs} {transit}061000227{transit} 2078288384{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("61000227", 0.88, 0.12, 0.10, 0.14, 0.82, 0.96),
            new ReadOcrLine(micr, 0.5, 0.88, 0.86, 0.90, 0.1, 0.9)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("002594", cn);
    }

    /// <summary>版头印刷 2594 + 右上误读 61000227 + MICR 002594：应输出展示形 2594，且不误吸 ABA 去零串。</summary>
    [Fact]
    public void ParseCheckNumber_WellsMaconStyle_PrefersHeader2594_OverRoutingTrimArtifact()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micr = $"{onUs}002594{onUs} {transit}061000227{transit} 2078288384{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("MACON WINGS INC", 0.35, 0.06, 0.05, 0.07, 0.08, 0.55),
            new ReadOcrLine("2594", 0.35, 0.11, 0.10, 0.12, 0.12, 0.42),
            new ReadOcrLine("64-22/610", 0.22, 0.22, 0.20, 0.24, 0.05, 0.45),
            new ReadOcrLine("61000227", 0.88, 0.12, 0.10, 0.14, 0.82, 0.96),
            new ReadOcrLine(micr, 0.5, 0.90, 0.88, 0.92, 0.08, 0.95)
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1000, 1000);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("2594", cn);
    }

    [Fact]
    public void ParseCheckNumber_MetroCityBankSplitMicr_PrefersLeadingBracketSixOverTrailingSevenDigits()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{onUs}003122{onUs} {transit}061120686{transit}";
        var micrLine2 = $"2037489{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("3122", 0.12, 0.06, 0.05, 0.07, 0.08, 0.18),
            new ReadOcrLine(micrLine1, 0.48, 0.88, 0.86, 0.90, 0.08, 0.92),
            new ReadOcrLine(micrLine2, 0.48, 0.93, 0.92, 0.94, 0.08, 0.92)
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("3122", cn);
    }

    /// <summary>无版头印刷序号时，磁墨两行「⑥ + ⑦」且 TryAssign 放弃：应取较短 bracket 序号为支票号。</summary>
    [Fact]
    public void ParseCheckNumber_MetroCityMicrOnly_ShortBracketSixDigitsBeatsTrailingSevenOnUs()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{onUs}003122{onUs} {transit}061120686{transit}";
        var micrLine2 = $"2037489{onUs}";
        var lines = new[]
        {
            new ReadOcrLine(micrLine1, 0.48, 0.88, 0.86, 0.90, 0.08, 0.92),
            new ReadOcrLine(micrLine2, 0.48, 0.93, 0.92, 0.94, 0.08, 0.92)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("003122", cn);
    }

    [Fact]
    public void ParseMicrHeuristic_TransitFragmentMerge_CommercialBankStyleSplitMicr()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micr = $"{transit}064202983{transit}1630\n719 1{onUs}\n0535";
        var r = CheckOcrVisionReadParser.ParseMicrHeuristic(micr);
        Assert.Equal("064202983", r.RoutingNumber);
        Assert.Equal("16307191", r.AccountNumber);
        Assert.True(r.RoutingAbaChecksumValid);
        Assert.Equal("e13b_transit_fragment_merge", r.RoutingSelectionMode);
    }

    [Fact]
    public void TryResolveMicrLineRawFromLayout_IncludesAdjacentDigitOnlyMicrBandLine()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{transit}064202983{transit}1630";
        var micrLine2 = $"719 1{onUs}";
        var micrLine3 = "0535";
        var lines = new[]
        {
            new ReadOcrLine(micrLine1, 0.5, 0.88, 0.86, 0.90, 0.1, 0.9),
            new ReadOcrLine(micrLine2, 0.5, 0.91, 0.89, 0.93, 0.1, 0.9),
            new ReadOcrLine(micrLine3, 0.5, 0.94, 0.93, 0.95, 0.1, 0.9),
        };
        var layout = new ReadOcrLayout(string.Join('\n', lines.Select(l => l.Text)), lines, 1200, 900);
        var raw = CheckOcrVisionReadParser.TryResolveMicrLineRawFromLayout(layout, "064202983");
        Assert.NotNull(raw);
        Assert.Contains("0535", raw, StringComparison.Ordinal);
        Assert.Contains("064202983", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCheckNumber_TransitFragmentMerge_AlignsPrintedThreeDigit535()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{transit}064202983{transit}1630";
        var micrLine2 = $"719 1{onUs}";
        var micrLine3 = "0535";
        var lines = new[]
        {
            new ReadOcrLine("Hungry Sumo", 0.3, 0.06, 0.05, 0.07, 0.1, 0.6),
            new ReadOcrLine("4126.26", 0.18, 0.08, 0.07, 0.09, 0.1, 0.22),
            new ReadOcrLine("535", 0.15, 0.10, 0.09, 0.11, 0.08, 0.22),
            new ReadOcrLine(micrLine1, 0.48, 0.88, 0.86, 0.90, 0.05, 0.92),
            new ReadOcrLine(micrLine2, 0.48, 0.91, 0.89, 0.93, 0.05, 0.92),
            new ReadOcrLine(micrLine3, 0.48, 0.94, 0.93, 0.95, 0.05, 0.92),
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("535", cn);
    }

    /// <summary>MICR 纯数字支票行未进入 ParseCheckNumber 所用 micr 文本时，勿把下行 mid⑈（7191）当支票号；应收印刷 535。</summary>
    [Fact]
    public void ParseCheckNumber_RejectsForMemoSevenDigit_OverPrinted535_WithMicr0535Hint()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{transit}064202983{transit}1630";
        var micrLine2 = $"719 1{onUs}";
        var micrLine3 = "0535";
        var lines = new[]
        {
            new ReadOcrLine("Hungry Sumo Hibachi House Lic", 0.22, 0.06, 0.05, 0.07, 0.08, 0.72),
            new ReadOcrLine("535", 0.18, 0.10, 0.09, 0.11, 0.08, 0.22),
            new ReadOcrLine("FOR I 2006586", 0.35, 0.52, 0.50, 0.54, 0.08, 0.42),
            new ReadOcrLine(micrLine1, 0.48, 0.88, 0.86, 0.90, 0.05, 0.92),
            new ReadOcrLine(micrLine2, 0.48, 0.91, 0.89, 0.93, 0.05, 0.92),
            new ReadOcrLine(micrLine3, 0.48, 0.94, 0.93, 0.95, 0.05, 0.92),
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("535", cn);
    }

    /// <summary><c>⑆ABA⑆ account⑈</c> 下一行纯数字磁墨支票序（如 <c>02728</c>），勿将 <c>21035555⑈</c> 当支票号。</summary>
    [Fact]
    public void ParseCheckNumber_TransitClosedAccountThenDigitLine02728_AlignsPrinted2728()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{transit}053104568{transit} 21035555{onUs}";
        var micrLine2 = "02728";
        var lines = new[]
        {
            new ReadOcrLine("YU LIN", 0.2, 0.05, 0.04, 0.06, 0.1, 0.4),
            new ReadOcrLine("2728", 0.15, 0.08, 0.07, 0.09, 0.1, 0.2),
            new ReadOcrLine(micrLine1, 0.5, 0.88, 0.86, 0.90, 0.05, 0.95),
            new ReadOcrLine(micrLine2, 0.5, 0.91, 0.90, 0.92, 0.05, 0.95),
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("2728", cn);
    }

    /// <summary>Wells 类单行 MICR：<c>⑆ABA⑆ account⑈ 01023</c>，勿将账号当作支票号。</summary>
    [Fact]
    public void ParseCheckNumber_WellsMicrSameLine01023_AlignsPrinted1023()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrOneLine = $"{transit}061000227{transit} 2710753324{onUs} 01023";
        var lines = new[]
        {
            new ReadOcrLine("WINGS SPOT INC", 0.22, 0.06, 0.05, 0.07, 0.08, 0.72),
            new ReadOcrLine("1023", 0.18, 0.09, 0.08, 0.10, 0.08, 0.22),
            new ReadOcrLine(micrOneLine, 0.5, 0.90, 0.88, 0.92, 0.04, 0.96),
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("1023", cn);
    }

    /// <summary>Navy Federal：<c>⑆ABA⑆0511⑉7208453105⑈001</c>（⑉ 常为 Read 替代 on-us）；勿取长账号作支票号。</summary>
    [Fact]
    public void ParseCheckNumber_NavyFedTransit0511DelimiterLongAccount_AlignsPrinted511()
    {
        const char transit = '\u2446';
        const char delimAux = '\u2449'; // 「⑉」
        const char onUs = '\u2448';
        var micrOneLine = $"{transit}256074974{transit}0511{delimAux}7208453105{onUs}001";
        var lines = new[]
        {
            new ReadOcrLine("TD9 LLC", 0.22, 0.06, 0.05, 0.07, 0.08, 0.72),
            new ReadOcrLine("511", 0.18, 0.09, 0.08, 0.10, 0.08, 0.22),
            new ReadOcrLine(micrOneLine, 0.5, 0.90, 0.88, 0.92, 0.04, 0.96),
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("511", cn);
    }

    [Fact]
    public void ParseCheckNumber_TransitFragmentMerge_Printed535WhenPureMicrCheckRowOutsideMicrSlice()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{transit}064202983{transit}1630";
        var micrLine2 = $"719 1{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("Hungry Sumo", 0.3, 0.06, 0.05, 0.07, 0.1, 0.6),
            new ReadOcrLine("535", 0.15, 0.10, 0.09, 0.11, 0.08, 0.22),
            new ReadOcrLine(micrLine1, 0.48, 0.88, 0.86, 0.90, 0.05, 0.92),
            new ReadOcrLine(micrLine2, 0.48, 0.91, 0.89, 0.93, 0.05, 0.92),
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1200, 900);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("535", cn);
    }

    [Fact]
    public void ParseCheckNumber_SkipsZipAndUsesLastOnUsBlock()
    {
        const char transit = '\u2446';
        const char onUs = '\u2448';
        var micrLine1 = $"{onUs}001408{onUs} {transit}021000021{transit}";
        var micrLine2 = $"562203631{onUs}";
        var lines = new[]
        {
            new ReadOcrLine("Summerville, SC 29485", 0.25, 0.14, 0.12, 0.16, 0.05, 0.45),
            new ReadOcrLine(micrLine1, 0.5, 0.88, 0.86, 0.90, 0.1, 0.9),
            new ReadOcrLine(micrLine2, 0.5, 0.92, 0.91, 0.93, 0.1, 0.9)
        };
        var full = string.Join("\n", lines.Select(l => l.Text));
        var layout = new ReadOcrLayout(full, lines, 1000, 1000);
        var (cn, _) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("562203631", cn);
    }

    [Fact]
    public void ParseBankName_RejectsCityStateZipLine()
    {
        var lines = new[]
        {
            new ReadOcrLine("Macon, GA 31201-1925", 0.12, 0.12, 0.10, 0.14, 0.05, 0.4),
            new ReadOcrLine("WELLS FARGO", 0.22, 0.14, 0.12, 0.16, 0.15, 0.42)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1000, 1000);
        var (bank, _) = CheckOcrVisionReadParser.ParseBankName(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("WELLS FARGO", bank);
    }

    /// <summary>左上像门牌+公路的行不应胜过磁墨上方的银行品牌（Regions 类版式）。</summary>
    [Fact]
    public void ParseBankName_PrefersMicrAdjacentBrandOverHighwayAddressLine()
    {
        var lines = new[]
        {
            new ReadOcrLine("SILVER SPRINGS CHINA KING LLC", 0.35, 0.08, 0.06, 0.10, 0.08, 0.72),
            new ReadOcrLine("15924 W HIGHWAY 40", 0.38, 0.12, 0.10, 0.14, 0.08, 0.72),
            new ReadOcrLine("SILVER SPRINGS, FL 34488", 0.35, 0.16, 0.14, 0.18, 0.08, 0.72),
            new ReadOcrLine("REGIONS", 0.28, 0.58, 0.56, 0.60, 0.08, 0.42)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1000, 1000);
        var (bank, _) = CheckOcrVisionReadParser.ParseBankName(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("REGIONS", bank);
    }

    [Fact]
    public void ParseBankName_RejectsHarlandClarkeSupplierLine()
    {
        var lines = new[]
        {
            new ReadOcrLine("CHASE BANK", 0.22, 0.14, 0.12, 0.16, 0.05, 0.45),
            new ReadOcrLine("Harland Clarke", 0.35, 0.62, 0.60, 0.64, 0.10, 0.50)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1000, 1000);
        var (bank, _) = CheckOcrVisionReadParser.ParseBankName(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("CHASE BANK", bank);
    }

    /// <summary>Chase 等票左上角的分数 transit（如 9-32/720）勿覆盖磁墨上方的完整法人银行名。</summary>
    [Fact]
    public void ParseBankName_RejectsPrintedFractionalTransitForChaseStyleLayout()
    {
        var lines = new[]
        {
            new ReadOcrLine("WOW COW LLC", 0.32, 0.08, 0.06, 0.10, 0.08, 0.62),
            new ReadOcrLine("9-32/720", 0.32, 0.12, 0.10, 0.14, 0.08, 0.62),
            new ReadOcrLine("CHASE O", 0.26, 0.56, 0.54, 0.58, 0.08, 0.42),
            new ReadOcrLine("JPMorgan Chase Bank, N.A.", 0.38, 0.62, 0.58, 0.66, 0.06, 0.58)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1000, 1000);
        var (bank, _) = CheckOcrVisionReadParser.ParseBankName(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("JPMorgan Chase Bank, N.A.", bank);
    }

    /// <summary>独立日期行与 Prior/Aux「断层」：法人银行行 normY≈0.32 时仍能进入候选，日期行不得占位。</summary>
    [Fact]
    public void ParseBankName_RejectsStandaloneDate_when_LegalBankSitsInMicrAdjacentGapBand()
    {
        var lines = new[]
        {
            new ReadOcrLine("WOW COW LLC", 0.32, 0.09, 0.07, 0.11, 0.08, 0.62),
            new ReadOcrLine("04/07/2026", 0.28, 0.18, 0.16, 0.20, 0.05, 0.35),
            new ReadOcrLine("CHASE O", 0.26, 0.32, 0.30, 0.34, 0.06, 0.42),
            new ReadOcrLine("JPMorgan Chase Bank, N.A.", 0.40, 0.36, 0.34, 0.38, 0.06, 0.55)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1000, 1000);
        var (bank, _) = CheckOcrVisionReadParser.ParseBankName(layout, CheckOcrParsingProfile.Default);
        Assert.Equal("JPMorgan Chase Bank, N.A.", bank);
    }

    [Fact]
    public void ParseDate_PrefersDateKeywordLineOverGarbageInlineDate()
    {
        var lines = new[]
        {
            new ReadOcrLine("DATE 3/30/26", 0.12, 0.16, 0.14, 0.18, 0.05, 0.35),
            new ReadOcrLine("Inte 2/9/08/", 0.12, 0.28, 0.26, 0.30, 0.05, 0.35)
        };
        var layout = new ReadOcrLayout(string.Join("\n", lines.Select(l => l.Text)), lines, 1000, 1000);
        var (dt, _) = CheckOcrVisionReadParser.ParseDate(layout, CheckOcrParsingProfile.Default);
        Assert.NotNull(dt);
        Assert.Equal(3, dt.Value.Month);
        Assert.Equal(30, dt.Value.Day);
    }

    [Fact]
    public void ParseMicrHeuristicBottomBand_PicksRoutingFromBottomArea()
    {
        var lines = new[]
        {
            new ReadOcrLine("noise 123456789", 0.2, 0.35, 0.33, 0.37, 0.1, 0.4),
            new ReadOcrLine("O21OO0021 12345678901234 4829", 0.52, 0.86, 0.84, 0.88, 0.1, 0.95)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);
        var result = CheckOcrVisionReadParser.ParseMicrHeuristicBottomBand(layout, 0.8);
        Assert.Equal("021000021", result.RoutingNumber);
        Assert.True(result.RoutingAbaChecksumValid);
        Assert.Contains("bottom_band", result.RoutingSelectionMode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsValidIbanMod97_AcceptsKnownTestIban()
    {
        Assert.True(CheckOcrEuInstrumentParser.IsValidIbanMod97("DE89370400440532013000"));
        Assert.False(CheckOcrEuInstrumentParser.IsValidIbanMod97("DE89370400440532013001"));
    }

    [Fact]
    public void TryFindValidIban_FindsCompactIbanInText()
    {
        var iban = CheckOcrEuInstrumentParser.TryFindValidIban("PAY DE89370400440532013000 END");
        Assert.Equal("DE89370400440532013000", iban);
    }

    [Fact]
    public void TryFindValidIban_FindsSpacedIbanInText()
    {
        var text = "Pay to IBAN DE89 3704 0044 0532 0130 00 end";
        var iban = CheckOcrEuInstrumentParser.TryFindValidIban(text);
        Assert.Equal("DE89370400440532013000", iban);
    }

    [Fact]
    public void TryFindBic_FindsSwiftLikeCode()
    {
        var text = "BIC COBADEFFXXX for transfer";
        Assert.Equal("COBADEFFXXX", CheckOcrEuInstrumentParser.TryFindBic(text));
    }
}
