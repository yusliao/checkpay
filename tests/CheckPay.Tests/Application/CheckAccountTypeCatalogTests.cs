using CheckPay.Application.Common;

namespace CheckPay.Tests.Application;

public class CheckAccountTypeCatalogTests
{
    [Theory]
    [InlineData("Business Checking", "Business Checking")]
    [InlineData("business checking", "Business Checking")]
    [InlineData("Savings", "Savings")]
    [InlineData("SAVINGS", "Savings")]
    [InlineData("Personal Savings", "Savings")]
    [InlineData("Checking", "Business Checking")]
    [InlineData("Business account", "Business Checking")]
    public void MatchSalesSelectable_maps_common_ocr_phrases(string raw, string expected)
        => Assert.Equal(expected, CheckAccountTypeCatalog.MatchSalesSelectable(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Money Market")]
    public void MatchSalesSelectable_returns_null_when_unknown(string? raw)
        => Assert.Null(CheckAccountTypeCatalog.MatchSalesSelectable(raw));
}
