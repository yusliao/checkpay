using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

namespace CheckPay.Tests.Domain;

public class DebitRecordTests
{
    [Fact]
    public void DebitRecord_ShouldInitializeWithDefaultValues()
    {
        var debit = new DebitRecord();

        Assert.NotEqual(Guid.Empty, debit.Id);
        Assert.Equal(DebitStatus.Unmatched, debit.DebitStatus);
        Assert.True(debit.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public void DebitRecord_ShouldSetProperties()
    {
        var customerId = Guid.NewGuid();
        var checkRecordId = Guid.NewGuid();
        var debit = new DebitRecord
        {
            CustomerId = customerId,
            CheckNumber = "67890",
            DebitAmount = 2500.75m,
            DebitDate = DateTime.Today,
            BankReference = "BNK123456",
            DebitStatus = DebitStatus.Matched,
            CheckRecordId = checkRecordId
        };

        Assert.Equal("67890", debit.CheckNumber);
        Assert.Equal(2500.75m, debit.DebitAmount);
        Assert.Equal(customerId, debit.CustomerId);
        Assert.Equal(checkRecordId, debit.CheckRecordId);
        Assert.Equal(DebitStatus.Matched, debit.DebitStatus);
    }

    [Fact]
    public void DebitRecord_ShouldAllowNullCheckRecordId()
    {
        var debit = new DebitRecord
        {
            CheckNumber = "99999",
            DebitStatus = DebitStatus.Unmatched,
            CheckRecordId = null
        };

        Assert.Null(debit.CheckRecordId);
        Assert.Equal(DebitStatus.Unmatched, debit.DebitStatus);
    }
}
