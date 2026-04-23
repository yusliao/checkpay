using CheckPay.Application.Common;

namespace CheckPay.Tests.Application;

public class PayToOrderOfCatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MatchCanonical_Empty_ReturnsNull(string? ocr) =>
        Assert.Null(PayToOrderOfCatalog.MatchCanonical(ocr));

    [Fact]
    public void MatchCanonical_ExactAlliance_ReturnsCanonical() =>
        Assert.Equal(
            PayToOrderOfCatalog.CheungKongAllianceFood,
            PayToOrderOfCatalog.MatchCanonical(
                "cheung kong holding holding inc. dba alliance food group"));

    [Fact]
    public void MatchCanonical_ExactMaxwell_ReturnsCanonical() =>
        Assert.Equal(
            PayToOrderOfCatalog.MaxwellExcelFood,
            PayToOrderOfCatalog.MatchCanonical(
                "maxwell trading inc. dba excel food services"));

    [Theory]
    [InlineData("Pay to ALLIANCE FOOD GROUP", PayToOrderOfCatalog.CheungKongAllianceFood)]
    [InlineData("CK ALLIANCE something", PayToOrderOfCatalog.CheungKongAllianceFood)]
    [InlineData("MAXWELL TRADING CO", PayToOrderOfCatalog.MaxwellExcelFood)]
    [InlineData("EXCEL FOOD SERVICES partial", PayToOrderOfCatalog.MaxwellExcelFood)]
    public void MatchCanonical_PartialOcr_Maps(string ocr, string expected) =>
        Assert.Equal(expected, PayToOrderOfCatalog.MatchCanonical(ocr));

    [Fact]
    public void MatchCanonical_Unrelated_ReturnsNull() =>
        Assert.Null(PayToOrderOfCatalog.MatchCanonical("RANDOM PAYEE LLC"));
}
