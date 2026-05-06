using System;
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
    [InlineData("Ten thousand one hundred and yourty eight por DOLLARS", 10148.00)]
    [InlineData("One thousand and 56/100 percent dollars", 1000.56)]
    [InlineData("One thousand and 56／100％ dollars", 1000.56)]
    public void TryParseAmountFromWords_ShouldParseExpectedAmount(string input, decimal expected)
    {
        var ok = AzureOcrService.TryParseAmountFromWords(input, out var actual);

        Assert.True(ok);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1014850, 10148.00, 10148.50)]
    [InlineData(1014854, 10148.00, 10148.54)]
    public void RescaleSuspectDiMinorUnitsToDollars_WhenConcatMinorUnits_AdjustsToDecimalDollars(
        decimal parsed,
        decimal hint,
        decimal expected)
    {
        Assert.Equal(expected, AzureOcrService.RescaleSuspectDiMinorUnitsToDollars(parsed, hint));
    }

    [Fact]
    public void FormatLegalAmountRawForDiagnosticsDisplay_inserts_fraction_from_parsed_cents()
    {
        const string raw = "Ten thousand one hundred and fourty eight 100\r\nDOLLARS";
        var s = AzureOcrService.FormatLegalAmountRawForDiagnosticsDisplay(raw, 10148.54m, "");
        Assert.Contains("54/100", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAmountFromWords_DiOrphan100_plusVision54_mergedContext_AppendsCents()
    {
        const string diChunk = "Ten thousand one hundred and fourty eight 100\r\nDOLLARS";
        var merged = $"""
                     $ 10148 00
                     {diChunk}
                     54/100
                     """;
        Assert.True(AzureOcrService.TryParseAmountFromWords(diChunk, out var a, merged));
        Assert.Equal(10148.54m, a);
    }

    [Fact]
    public void TryParseAmountFromWords_BareSpilled54_aboveDollarBox_noStandaloneSlashLine()
    {
        const string line = "Ten thousand one hundred and yourty eight por DOLLARS";
        const string merged = """
            PAY
            54
            TO THE
            $ 10148 00
            ORDER OF
            Ten thousand one hundred and yourty eight por DOLLARS
            """;
        Assert.True(AzureOcrService.TryParseAmountFromWords(line, out var a, merged));
        Assert.Equal(10148.54m, a);
    }

    [Fact]
    public void RefineLegalCentsAgainstVisionSpilledLine_OverridesDiMinorConcat50_withBare54Band()
    {
        const string merged = """
            PAY
            54
            TO THE
            $ 10148 00
            ORDER OF
            X
            """;
        var r = AzureOcrService.RefineLegalCentsAgainstVisionSpilledLine(10148.50m, 10148.00m, merged);
        Assert.Equal(10148.54m, r);
    }

    [Fact]
    public void TryParseAmountFromWords_DiOrphanNewline100_ReturnsPrintedDollars()
    {
        var s = "Ten thousand one hundred and fourty eight 100\r\nDOLLARS";
        Assert.True(AzureOcrService.TryParseAmountFromWords(s, out var a, null));
        Assert.Equal(10148.00m, a);
    }

    [Fact]
    public void TryParseAmountFromWords_WithStandalonePercentLine_AppendsHundredthsFromFullText()
    {
        const string line = "Ten thousand one hundred and yourty eight por DOLLARS";
        const string fullText = """
            $ 10148 00
            Ten thousand one hundred and yourty eight por DOLLARS
            54/100
            """;
        Assert.True(AzureOcrService.TryParseAmountFromWords(line, out var a, fullText));
        Assert.Equal(10148.54m, a);
    }

    [Fact]
    public void TryParseBestWrittenAmountFromCheckFullText_PicksLegalLineOverNoise()
    {
        const string rawText = """
            $ 10148 00
            Ten thousand one hundred and yourty eight por DOLLARS
            54/100
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
            54/100
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
