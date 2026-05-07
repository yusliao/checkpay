using CheckPay.Application.Common;
using CheckPay.Domain.Entities;
using ClosedXML.Excel;

namespace CheckPay.Tests.Application;

public class AchBankImportExportXlsxTests
{
    [Fact]
    public void Build_Empty_StillHasHeaderRow()
    {
        var bytes = AchBankImportExportXlsx.Build(Array.Empty<CheckRecord>());
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("ACH");
        Assert.Equal("ABA", ws.Cell(1, 1).GetString());
        Assert.Equal("Amount", ws.Cell(1, 6).GetString());
    }

    [Fact]
    public void Build_PreservesLeadingZeros_OnRoutingAndAccount()
    {
        var rows = new[]
        {
            new CheckRecord
            {
                RoutingNumber = "061000104",
                AccountNumber = "001234567890",
                AccountType = null,
                AccountHolderName = "Test LLC",
                CheckAmount = 100.50m,
                Customer = new Customer { MobilePhone = "0123456789" }
            }
        };

        var bytes = AchBankImportExportXlsx.Build(rows);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("ACH");

        Assert.Equal("061000104", ws.Cell(2, 1).GetFormattedString());
        Assert.Equal("001234567890", ws.Cell(2, 2).GetFormattedString());
        Assert.Equal("Checking", ws.Cell(2, 3).GetFormattedString());
        Assert.Equal("Test LLC", ws.Cell(2, 4).GetFormattedString());
        Assert.Equal("0123456789", ws.Cell(2, 5).GetFormattedString());
        Assert.Equal(100.50, ws.Cell(2, 6).GetDouble(), precision: 5);
        Assert.True(ws.Column(4).Width >= 40, "Name column should use a wide preset width");
    }
}
