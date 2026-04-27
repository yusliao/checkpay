using CheckPay.Application.Common.Models;
using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public class CheckOcrVisionReadParserTests
{
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
}
