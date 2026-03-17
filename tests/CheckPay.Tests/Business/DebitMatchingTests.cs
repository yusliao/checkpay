using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Business;

/// <summary>
/// 测试DebitImport.razor中的扣款匹配逻辑
/// 核心规则：支票号匹配 + 状态必须是PendingDebit
/// </summary>
public class DebitMatchingTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Customer MakeCustomer() => new()
    {
        Id = Guid.NewGuid(),
        CustomerCode = "C001",
        CustomerName = "测试客户",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static CheckRecord MakeCheckRecord(string checkNumber, CheckStatus status, Guid customerId) => new()
    {
        Id = Guid.NewGuid(),
        CheckNumber = checkNumber,
        CheckAmount = 1000m,
        CheckDate = DateTime.Today,
        CustomerId = customerId,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Match_ShouldSucceed_WhenCheckNumberMatches_AndStatusIsPendingDebit()
    {
        await using var ctx = CreateContext();
        var customer = MakeCustomer();
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(MakeCheckRecord("12345", CheckStatus.PendingDebit, customer.Id));
        await ctx.SaveChangesAsync();

        // 模拟页面匹配逻辑
        var matched = await ctx.CheckRecords
            .FirstOrDefaultAsync(c => c.CheckNumber == "12345" && c.Status == CheckStatus.PendingDebit);

        Assert.NotNull(matched);
    }

    [Fact]
    public async Task Match_ShouldFail_WhenCheckNumberDoesNotExist()
    {
        await using var ctx = CreateContext();
        var customer = MakeCustomer();
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(MakeCheckRecord("12345", CheckStatus.PendingDebit, customer.Id));
        await ctx.SaveChangesAsync();

        var matched = await ctx.CheckRecords
            .FirstOrDefaultAsync(c => c.CheckNumber == "99999" && c.Status == CheckStatus.PendingDebit);

        Assert.Null(matched); // 支票号不存在，匹配失败
    }

    [Fact]
    public async Task Match_ShouldFail_WhenCheckStatusIsNotPendingDebit()
    {
        await using var ctx = CreateContext();
        var customer = MakeCustomer();
        ctx.Customers.Add(customer);
        // 状态已经是PendingReview，不应再被匹配
        ctx.CheckRecords.Add(MakeCheckRecord("12345", CheckStatus.PendingReview, customer.Id));
        await ctx.SaveChangesAsync();

        var matched = await ctx.CheckRecords
            .FirstOrDefaultAsync(c => c.CheckNumber == "12345" && c.Status == CheckStatus.PendingDebit);

        Assert.Null(matched); // 状态不对，匹配失败
    }

    [Fact]
    public async Task SaveDebit_ShouldSetMatchedStatus_WhenCheckFound()
    {
        await using var ctx = CreateContext();
        var customer = MakeCustomer();
        ctx.Customers.Add(customer);
        var checkRecord = MakeCheckRecord("12345", CheckStatus.PendingDebit, customer.Id);
        ctx.CheckRecords.Add(checkRecord);
        await ctx.SaveChangesAsync();

        var matched = await ctx.CheckRecords
            .FirstOrDefaultAsync(c => c.CheckNumber == "12345" && c.Status == CheckStatus.PendingDebit);

        // 模拟页面保存逻辑
        var debit = new DebitRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CheckNumber = "12345",
            DebitAmount = 1000m,
            DebitDate = DateTime.Today,
            BankReference = "REF001",
            DebitStatus = matched != null ? DebitStatus.Matched : DebitStatus.Unmatched,
            CheckRecordId = matched?.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        if (matched != null)
            matched.Status = CheckStatus.PendingReview;

        ctx.DebitRecords.Add(debit);
        await ctx.SaveChangesAsync();

        Assert.Equal(DebitStatus.Matched, debit.DebitStatus);
        Assert.Equal(checkRecord.Id, debit.CheckRecordId);

        var updatedCheck = await ctx.CheckRecords.FirstAsync();
        Assert.Equal(CheckStatus.PendingReview, updatedCheck.Status); // 支票状态已更新
    }

    [Fact]
    public async Task SaveDebit_ShouldSetUnmatchedStatus_WhenNoCheckFound()
    {
        await using var ctx = CreateContext();
        var customer = MakeCustomer();
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        CheckRecord? matched = null; // 未找到匹配

        var debit = new DebitRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CheckNumber = "99999",
            DebitAmount = 500m,
            DebitDate = DateTime.Today,
            BankReference = "REF002",
            DebitStatus = matched != null ? DebitStatus.Matched : DebitStatus.Unmatched,
            CheckRecordId = matched?.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ctx.DebitRecords.Add(debit);
        await ctx.SaveChangesAsync();

        Assert.Equal(DebitStatus.Unmatched, debit.DebitStatus);
        Assert.Null(debit.CheckRecordId); // 未匹配时没有关联支票
    }
}
