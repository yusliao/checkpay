using CheckPay.Application.Common;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Business;

public class AchDebitSuccessMarkRevocationTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task TryRevokeAsync_ShouldClearSuccess_AndSetRevokedAt_WhenPendingReviewAndSucceeded()
    {
        await using var ctx = CreateContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C1",
            CustomerName = "N",
            MobilePhone = "13800000000",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var checkId = Guid.NewGuid();
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(new CheckRecord
        {
            Id = checkId,
            CustomerId = customer.Id,
            CheckNumber = "CHK1",
            CheckAmount = 100m,
            CheckDate = DateTime.UtcNow.Date,
            Status = CheckStatus.PendingReview,
            AchDebitSucceeded = true,
            AchDebitSucceededAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var result = await AchDebitSuccessMarkRevocation.TryRevokeAsync(ctx, checkId);

        Assert.True(result.Success);
        var check = await ctx.CheckRecords.FirstAsync(c => c.Id == checkId);
        Assert.False(check.AchDebitSucceeded);
        Assert.Null(check.AchDebitSucceededAt);
        Assert.NotNull(check.AchDebitSuccessRevokedAt);
    }

    [Fact]
    public async Task TryRevokeAsync_ShouldFail_WhenNotSucceeded()
    {
        await using var ctx = CreateContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C1",
            CustomerName = "N",
            MobilePhone = "13800000000",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var checkId = Guid.NewGuid();
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(new CheckRecord
        {
            Id = checkId,
            CustomerId = customer.Id,
            CheckNumber = "CHK1",
            CheckAmount = 100m,
            CheckDate = DateTime.UtcNow.Date,
            Status = CheckStatus.PendingReview,
            AchDebitSucceeded = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var result = await AchDebitSuccessMarkRevocation.TryRevokeAsync(ctx, checkId);

        Assert.False(result.Success);
    }
}
