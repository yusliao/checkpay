using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public class AzureOcrAmountParserTests
{
    [Theory]
    [InlineData("One thousand two hundred thirty four and 56/100 dollars", 1234.56)]
    [InlineData("Nine hundred only", 900.00)]
    [InlineData("Twenty-five and 01/100", 25.01)]
    [InlineData("One million and 00/100 dollars", 1000000.00)]
    [InlineData("Six Hundred Eightysix 25!", 686.25)]
    [InlineData("Two thousandh forty six dollar & 3.9 /00", 2046.39)]
    [InlineData("Ten thousand one hundred and yourty eight por DOLLARS", 10148.54)]
    [InlineData("One thousand and 56/100 percent dollars", 1000.56)]
    [InlineData("One thousand and 56／100％ dollars", 1000.56)]
    public void TryParseAmountFromWords_ShouldParseExpectedAmount(string input, decimal expected)
    {
        var ok = AzureOcrService.TryParseAmountFromWords(input, out var actual);

        Assert.True(ok);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParseBestWrittenAmountFromCheckFullText_PicksLegalLineOverNoise()
    {
        const string rawText = """
            $ 10148 00
            Ten thousand one hundred and yourty eight por DOLLARS
            Safe
            """;
        Assert.True(AzureOcrService.TryParseBestWrittenAmountFromCheckFullText(rawText, out var a));
        Assert.Equal(10148.54m, a);
    }

    [Fact]
    public void ApplyWrittenAmountAugmentationFromVisionText_Replaces00WithWrittenCents()
    {
        const string rawText = """
            $ 10148 00
            Ten thousand one hundred and yourty eight por DOLLARS
            """;
        var amt = 10148.00m;
        var conf = 0.62;
        Assert.True(AzureOcrService.ApplyWrittenAmountAugmentationFromVisionText(rawText, ref amt, ref conf));
        Assert.Equal(10148.54m, amt);
        Assert.True(conf >= 0.64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("Pay to the order of")]
    public void TryParseAmountFromWords_ShouldReturnFalse_ForInvalidText(string input)
    {
        var ok = AzureOcrService.TryParseAmountFromWords(input, out _);
        Assert.False(ok);
    }
}
