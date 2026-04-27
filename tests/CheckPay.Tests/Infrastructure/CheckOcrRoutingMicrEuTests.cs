using CheckPay.Infrastructure.Services;

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
