using CheckPay.Application.Common.Models;
using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public class CheckOcrVisionReadParserTests
{
    [Fact]
    public void ParseCheckNumber_PrefersTopRightPrintedCandidateWhenMicrConflicts()
    {
        var lines = new[]
        {
            new ReadOcrLine("Check No. 824901", 0.86, 0.10, 0.08, 0.12, 0.76, 0.96),
            new ReadOcrLine("Date 03/15/2024", 0.12, 0.10, 0.08, 0.12, 0.05, 0.30),
            new ReadOcrLine("123456789 1234567890 123456", 0.56, 0.90, 0.86, 0.94, 0.18, 0.94)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (checkNumber, confidence) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("824901", checkNumber);
        Assert.True(confidence >= 0.66);
    }

    [Fact]
    public void ParseCheckNumber_AvoidsPickingAmountAsPrintedCheckNumber()
    {
        var lines = new[]
        {
            new ReadOcrLine("$1234.56", 0.84, 0.12, 0.10, 0.14, 0.76, 0.94),
            new ReadOcrLine("Check # 567890", 0.82, 0.18, 0.16, 0.20, 0.72, 0.94)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (checkNumber, confidence) = CheckOcrVisionReadParser.ParseCheckNumber(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("567890", checkNumber);
        Assert.True(confidence >= 0.62);
    }

    [Fact]
    public void ParseAmount_PrefersUpperRightDollarOverBottomMicrLikeAmount()
    {
        var lines = new[]
        {
            new ReadOcrLine("$1,234.56", 0.82, 0.14, 0.10, 0.18, 0.70, 0.94),
            new ReadOcrLine("9999.99", 0.50, 0.90, 0.86, 0.94, 0.40, 0.60)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);
        var (amount, _) = CheckOcrVisionReadParser.ParseAmount(layout, CheckOcrParsingProfile.Default);
        Assert.Equal(1234.56m, amount);
    }

    [Fact]
    public void ParseDate_PrefersLineInDatePriorRegion()
    {
        var lines = new[]
        {
            new ReadOcrLine("noise 12/31/2099", 0.85, 0.20, 0.15, 0.25, 0.75, 0.95),
            new ReadOcrLine("Date 03/15/2024", 0.22, 0.12, 0.08, 0.16, 0.10, 0.40)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);
        var (date, _) = CheckOcrVisionReadParser.ParseDate(layout, CheckOcrParsingProfile.Default);
        Assert.NotNull(date);
        Assert.Equal(2024, date.Value.Year);
        Assert.Equal(3, date.Value.Month);
        Assert.Equal(15, date.Value.Day);
    }

    [Fact]
    public void ParseBankName_PrefersTopLeftBankCandidate()
    {
        var lines = new[]
        {
            new ReadOcrLine("Date 03/15/2024", 0.18, 0.10, 0.08, 0.12, 0.05, 0.30),
            new ReadOcrLine("FIRST NATIONAL BANK", 0.25, 0.12, 0.10, 0.14, 0.05, 0.45),
            new ReadOcrLine("Pay to the order of JOHN DOE", 0.42, 0.40, 0.36, 0.44, 0.10, 0.70)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (bankName, confidence) = CheckOcrVisionReadParser.ParseBankName(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("FIRST NATIONAL BANK", bankName);
        Assert.True(confidence >= 0.7);
    }

    [Fact]
    public void ParseAccountHolderName_AvoidsAddressLikeLine()
    {
        var lines = new[]
        {
            new ReadOcrLine("123 Main Street", 0.20, 0.38, 0.34, 0.42, 0.05, 0.40),
            new ReadOcrLine("JOHN A DOE", 0.24, 0.44, 0.40, 0.48, 0.05, 0.42),
            new ReadOcrLine("Memo Payroll", 0.22, 0.52, 0.50, 0.54, 0.05, 0.36)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (holder, confidence) = CheckOcrVisionReadParser.ParseAccountHolderName(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("JOHN A DOE", holder);
        Assert.True(confidence >= 0.6);
    }

    [Fact]
    public void ParseAccountAddress_MergesNearbyAddressLines()
    {
        var lines = new[]
        {
            new ReadOcrLine("JOHN A DOE", 0.22, 0.44, 0.40, 0.48, 0.05, 0.42),
            new ReadOcrLine("123 Main Street", 0.24, 0.56, 0.52, 0.60, 0.06, 0.44),
            new ReadOcrLine("New York NY 10001", 0.26, 0.62, 0.60, 0.64, 0.06, 0.46)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (address, confidence) = CheckOcrVisionReadParser.ParseAccountAddress(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("123 Main Street, New York NY 10001", address);
        Assert.True(confidence >= 0.6);
    }

    [Fact]
    public void ParseAccountAddress_IgnoresNearbyAmountWordsLine()
    {
        var lines = new[]
        {
            new ReadOcrLine("123 Main Street", 0.24, 0.56, 0.52, 0.60, 0.06, 0.44),
            new ReadOcrLine("four thousand five hundred only", 0.24, 0.60, 0.58, 0.62, 0.06, 0.56),
            new ReadOcrLine("New York NY 10001", 0.26, 0.62, 0.60, 0.64, 0.06, 0.46)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (address, _) = CheckOcrVisionReadParser.ParseAccountAddress(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("123 Main Street, New York NY 10001", address);
    }

    [Fact]
    public void ParseAccountAddress_MergesStreetAndCityZipWithWiderNormYGap()
    {
        // Read 两行地址 normCenterY 间距可超过旧逻辑 0.12，仍应并成一条
        var lines = new[]
        {
            new ReadOcrLine("1033 RANDOLPH ST STE 9", 0.22, 0.36, 0.34, 0.38, 0.10, 0.48),
            new ReadOcrLine("THOMASVILLE NC 27360-5731", 0.22, 0.55, 0.52, 0.58, 0.10, 0.48)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (address, _) = CheckOcrVisionReadParser.ParseAccountAddress(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("1033 RANDOLPH ST STE 9, THOMASVILLE NC 27360-5731", address);
    }

    [Fact]
    public void ParseAccountAddress_SkipsIncCompanyLineAndMergesStreetCityZip()
    {
        var lines = new[]
        {
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.22, 0.32, 0.30, 0.34, 0.10, 0.48),
            new ReadOcrLine("1033 RANDOLPH ST STE 9", 0.22, 0.42, 0.40, 0.44, 0.10, 0.48),
            new ReadOcrLine("THOMASVILLE NC 27360-5731", 0.22, 0.52, 0.50, 0.54, 0.10, 0.48)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (address, _) = CheckOcrVisionReadParser.ParseAccountAddress(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("1033 RANDOLPH ST STE 9, THOMASVILLE NC 27360-5731", address);
    }

    [Fact]
    public void ParseAccountAddress_CompanyAnchoredWhenStreetCenterYBelow022()
    {
        // 商号行很靠上时，街道行 normCenterY 常 <0.22（旧默认 accountAddressPriorRegion 下沿），须靠「商号下方左栏」锚定
        var lines = new[]
        {
            new ReadOcrLine("1675", 0.88, 0.08, 0.06, 0.10, 0.72, 0.96),
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.42, 0.14, 0.12, 0.16, 0.08, 0.78),
            new ReadOcrLine("1033 RANDOLPH ST STE 9", 0.42, 0.185, 0.17, 0.20, 0.08, 0.78),
            new ReadOcrLine("THOMASVILLE NC 27360-5731", 0.42, 0.255, 0.24, 0.27, 0.08, 0.78),
            new ReadOcrLine("BANK OF AMERICA", 0.35, 0.46, 0.44, 0.48, 0.12, 0.58)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (address, _) = CheckOcrVisionReadParser.ParseAccountAddress(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("1033 RANDOLPH ST STE 9, THOMASVILLE NC 27360-5731", address);
    }

    [Fact]
    public void ParseAccountAddress_RejectsForInvPayeeAndMicrOutsidePrintedBand()
    {
        // 真实版式：印刷地址在左上；中下 FOR INV / 付款人 / 底部 MICR 不应并入 account_address
        var lines = new[]
        {
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.42, 0.14, 0.12, 0.16, 0.08, 0.78),
            new ReadOcrLine("1033 RANDOLPH ST STE 9", 0.42, 0.20, 0.18, 0.22, 0.08, 0.78),
            new ReadOcrLine("THOMASVILLE NC 27360-5731", 0.42, 0.27, 0.24, 0.30, 0.08, 0.78),
            new ReadOcrLine("FOR INV # 2203920 2207975", 0.40, 0.62, 0.58, 0.66, 0.12, 0.68),
            new ReadOcrLine("Allance food Group", 0.38, 0.70, 0.66, 0.74, 0.14, 0.62),
            new ReadOcrLine("⑈001675⑈ ⑆053000196⑆ 237051731175⑈", 0.50, 0.92, 0.88, 0.96, 0.08, 0.92)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (address, _) = CheckOcrVisionReadParser.ParseAccountAddress(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("1033 RANDOLPH ST STE 9, THOMASVILLE NC 27360-5731", address);
    }

    [Fact]
    public void ParseCompanyName_PrefersLineWithIncOverWeakerGroupStyleName()
    {
        var lines = new[]
        {
            new ReadOcrLine("ALLIANCE FOOD GROUP", 0.20, 0.36, 0.34, 0.38, 0.08, 0.42),
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.22, 0.42, 0.40, 0.44, 0.10, 0.48)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (company, confidence) = CheckOcrVisionReadParser.ParseCompanyName(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("168 CHINA GARDEN INC.", company);
        Assert.True(confidence >= 0.5);
    }

    [Fact]
    public void ParseCompanyName_IncludesTopPrintedIncAboveOldMinNormY()
    {
        // 旧默认 minNormY=0.20 会漏掉支票号下方、几何更靠上的一行商号
        var lines = new[]
        {
            new ReadOcrLine("1675", 0.88, 0.08, 0.06, 0.10, 0.72, 0.96),
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.42, 0.14, 0.12, 0.16, 0.08, 0.78),
            new ReadOcrLine("BANK OF AMERICA", 0.35, 0.46, 0.44, 0.48, 0.12, 0.58)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (company, _) = CheckOcrVisionReadParser.ParseCompanyName(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("168 CHINA GARDEN INC.", company);
    }

    [Fact]
    public void ParseAccountHolderName_RejectsBankBrandingPreferringPayeeLine()
    {
        var lines = new[]
        {
            new ReadOcrLine("BANK OF AMERICA", 0.40, 0.40, 0.38, 0.42, 0.10, 0.70),
            new ReadOcrLine("Allance food Group", 0.42, 0.48, 0.46, 0.50, 0.10, 0.72)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (holder, _) = CheckOcrVisionReadParser.ParseAccountHolderName(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("Allance food Group", holder);
    }

    [Fact]
    public void ParseCompanyName_FallbackFindsIncLineWhenOutsideTemplateCompanyBand()
    {
        // 票型把 company 带缩得过窄时，上半张回退仍应命中 INC 抬头
        var narrowCompanyBand = CheckOcrParsingProfile.MergeDefaults(new CheckOcrParsingProfile
        {
            CompanyNamePriorRegion = new NormRegion(0.0, 0.0, 0.45, 0.62)
        });
        var lines = new[]
        {
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.72, 0.15, 0.13, 0.17, 0.55, 0.88)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (company, _) = CheckOcrVisionReadParser.ParseCompanyName(layout, narrowCompanyBand);

        Assert.Equal("168 CHINA GARDEN INC.", company);
    }

    [Fact]
    public void ShouldSkipDiPayerNameForAccountHolder_WhenBankBrandingOrSameAsBank()
    {
        Assert.True(CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder("BANK OF AMERICA", "Bank Of America"));
        Assert.True(CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder("  bank of america  ", null));
        Assert.False(CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder("168 CHINA GARDEN INC.", "BANK OF AMERICA"));
        Assert.False(CheckOcrVisionReadParser.ShouldSkipDiPayerNameForAccountHolder("Allance food Group", "BANK OF AMERICA"));
    }

    [Fact]
    public void ParseCompanyName_UsesFullTextLineOrderWhenGeometryExcludesIncLine()
    {
        // 真实 Read 偶发把抬头行 normY 标到偏下，几何 ∪ 上半带仍够不到时，应回退到 FullText 行序
        var fullText = """
1675
168 CHINA GARDEN INC.
1033 RANDOLPH ST STE 9
BANK OF AMERICA
⑈001675⑈ ⑆053000196⑆ 237051731175⑈
""";
        var badLines = new[]
        {
            new ReadOcrLine("1675", 0.5, 0.88, 0.86, 0.90, 0.4, 0.6),
            new ReadOcrLine("168 CHINA GARDEN INC.", 0.5, 0.82, 0.80, 0.84, 0.4, 0.6),
            new ReadOcrLine("BANK OF AMERICA", 0.5, 0.48, 0.46, 0.50, 0.2, 0.7),
            new ReadOcrLine("⑈001675⑈ ⑆053000196⑆ 237051731175⑈", 0.5, 0.92, 0.90, 0.94, 0.1, 0.9)
        };
        var layout = new ReadOcrLayout(fullText, badLines, 1000, 1000);

        var (company, _) = CheckOcrVisionReadParser.ParseCompanyName(layout, CheckOcrParsingProfile.Default);

        Assert.Equal("168 CHINA GARDEN INC.", company);
    }

    [Fact]
    public void ParseCompanyName_RejectsPlainStreetLineWithoutLegalSuffix()
    {
        var lines = new[]
        {
            new ReadOcrLine("456 Oak Avenue", 0.30, 0.40, 0.38, 0.42, 0.08, 0.45)
        };
        var layout = new ReadOcrLayout("x", lines, 1000, 1000);

        var (company, confidence) = CheckOcrVisionReadParser.ParseCompanyName(layout, CheckOcrParsingProfile.Default);

        Assert.Null(company);
        Assert.True(confidence < 0.2);
    }
}
