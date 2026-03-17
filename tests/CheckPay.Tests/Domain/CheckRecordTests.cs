using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

namespace CheckPay.Tests.Domain;

public class CheckRecordTests
{
    [Fact]
    public void CheckRecord_ShouldInitializeWithDefaultValues()
    {
        var check = new CheckRecord();

        Assert.NotEqual(Guid.Empty, check.Id);
        Assert.Equal(CheckStatus.PendingDebit, check.Status);
        Assert.True(check.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public void CheckRecord_ShouldSetProperties()
    {
        var customerId = Guid.NewGuid();
        var check = new CheckRecord
        {
            CheckNumber = "12345",
            CheckAmount = 1000.50m,
            CheckDate = DateTime.Today,
            CustomerId = customerId
        };

        Assert.Equal("12345", check.CheckNumber);
        Assert.Equal(1000.50m, check.CheckAmount);
        Assert.Equal(customerId, check.CustomerId);
    }
}
