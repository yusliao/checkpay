using CheckPay.Application.Common;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Business;

public class PendingReviewDebitRevocationTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task TryRevokeAsync_ShouldUnlinkAndResetCheck_WhenPendingReviewWithDebit()
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
        var debitId = Guid.NewGuid();
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
            AchDebitSucceededAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        ctx.DebitRecords.Add(new DebitRecord
        {
            Id = debitId,
            CustomerId = customer.Id,
            CheckNumber = "CHK1",
            DebitAmount = 100m,
            DebitDate = DateTime.UtcNow,
            BankReference = "BR1",
            DebitStatus = DebitStatus.Matched,
            CheckRecordId = checkId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var result = await PendingReviewDebitRevocation.TryRevokeAsync(ctx, checkId);

        Assert.True(result.Success);
        var checkAfter = await ctx.CheckRecords.FirstAsync(c => c.Id == checkId);
        var debitAfter = await ctx.DebitRecords.FirstAsync(d => d.Id == debitId);
        Assert.Equal(CheckStatus.PendingDebit, checkAfter.Status);
        Assert.False(checkAfter.AchDebitSucceeded);
        Assert.Null(checkAfter.AchDebitSucceededAt);
        Assert.Null(checkAfter.AchDebitSuccessRevokedAt);
        Assert.Null(debitAfter.CheckRecordId);
        Assert.Equal(DebitStatus.Unmatched, debitAfter.DebitStatus);
    }

    [Fact]
    public async Task TryRevokeAsync_ShouldClearAchRevokedAt_WhenFullUnlink()
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
        var debitId = Guid.NewGuid();
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
            AchDebitSuccessRevokedAt = DateTime.UtcNow.AddHours(-2),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        ctx.DebitRecords.Add(new DebitRecord
        {
            Id = debitId,
            CustomerId = customer.Id,
            CheckNumber = "CHK1",
            DebitAmount = 100m,
            DebitDate = DateTime.UtcNow,
            BankReference = "BR1",
            DebitStatus = DebitStatus.Matched,
            CheckRecordId = checkId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var result = await PendingReviewDebitRevocation.TryRevokeAsync(ctx, checkId);
        Assert.True(result.Success);
        var checkAfter = await ctx.CheckRecords.FirstAsync(c => c.Id == checkId);
        Assert.Null(checkAfter.AchDebitSuccessRevokedAt);
    }

    [Fact]
    public async Task TryRevokeAsync_ShouldFail_WhenNoDebitLinked()
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
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var result = await PendingReviewDebitRevocation.TryRevokeAsync(ctx, checkId);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
