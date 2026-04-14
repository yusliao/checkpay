using CheckPay.Application.Common;
using CheckPay.Domain.Entities;

namespace CheckPay.Tests.Application;

public class CheckMasterDataComparerTests
{
    [Fact]
    public void HasMismatch_WhenBankDiffers_ReturnsTrue()
    {
        var c = new Customer { CustomerCode = "T1", CustomerName = "T", ExpectedBankName = "First Horizon" };
        Assert.True(CheckMasterDataComparer.HasMismatch(c, "Chase Bank", null));
    }

    [Fact]
    public void HasMismatch_WhenExpectedEmpty_ReturnsFalse()
    {
        var c = new Customer { CustomerCode = "X", CustomerName = "X" };
        Assert.False(CheckMasterDataComparer.HasMismatch(c, "Any Bank", "Anyone"));
    }

    [Fact]
    public void HasMismatch_WhenAccountNumberDiffers_ReturnsTrue()
    {
        var c = new Customer { CustomerCode = "123456789", CustomerName = "A" };
        Assert.True(CheckMasterDataComparer.HasMismatch(c, null, null, ocrAccountNumber: "987654321"));
    }

    [Fact]
    public void HasMismatch_WhenCompanyMatches_ReturnsFalse()
    {
        var c = new Customer
        {
            CustomerCode = "B",
            CustomerName = "B",
            ExpectedCompanyName = "Acme LLC"
        };
        Assert.False(CheckMasterDataComparer.HasMismatch(c, null, null, ocrCompanyName: "Acme LLC"));
    }
}
