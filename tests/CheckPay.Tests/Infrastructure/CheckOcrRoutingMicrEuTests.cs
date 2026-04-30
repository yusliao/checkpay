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
    public void ParseCheckNumber_SkipsZipPlus4AndUsesTenDigitExternalOnUs()
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
        Assert.Equal("2078288384", cn);
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
