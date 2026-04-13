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
}
