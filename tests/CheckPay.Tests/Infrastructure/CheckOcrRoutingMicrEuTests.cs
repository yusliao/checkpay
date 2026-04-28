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
