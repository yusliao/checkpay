using CheckPay.Application.Common;
using CheckPay.Domain.Entities;

namespace CheckPay.Tests.Application;

public class AchBankImportExportCsvTests
{
    [Fact]
    public void HeaderLine_MatchesBankTemplate_SixColumns()
    {
        Assert.Equal("ABA,Account number,Account Type,Name,Detail ID,Amount", AchBankImportExportCsv.HeaderLine);
    }

    [Fact]
    public void FormatDataRow_EmptyAccountType_UsesChecking()
    {
        var c = new CheckRecord
        {
            RoutingNumber = "44101305",
            AccountNumber = "11012142010",
            AccountType = null,
            AccountHolderName = "SUN KING LLC",
            CheckAmount = 9832.49m,
            Customer = new Customer { MobilePhone = "8646750888" }
        };

        var line = AchBankImportExportCsv.FormatDataRow(c);
        Assert.StartsWith("\"=\"\"44101305\"\"\",", line);
        Assert.Contains("\"=\"\"11012142010\"\"\",", line);
        Assert.Contains(",\"Checking\",", line);
        Assert.Contains("\"SUN KING LLC\"", line);
        Assert.Contains("\"=\"\"8646750888\"\"\",", line);
        Assert.EndsWith(",9832.49", line);
    }

    [Fact]
    public void FormatDataRow_PreservesNonEmptyAccountType()
    {
        var c = new CheckRecord
        {
            AccountType = "Business Checking",
            CheckAmount = 1m,
            Customer = new Customer()
        };

        var line = AchBankImportExportCsv.FormatDataRow(c);
        Assert.Contains(",\"Business Checking\",", line);
    }
}
