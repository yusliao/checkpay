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
    public void TryParseAmountFromWords_ShouldParseExpectedAmount(string input, decimal expected)
    {
        var ok = AzureOcrService.TryParseAmountFromWords(input, out var actual);

        Assert.True(ok);
        Assert.Equal(expected, actual);
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
