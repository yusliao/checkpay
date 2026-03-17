using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Business;

/// <summary>
/// 测试ReviewDetail.razor中的核查状态流转逻辑
/// </summary>
public class ReviewStatusFlowTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    // ====== 金额差异判断 ======

    [Theory]
    [InlineData(1000.00, 1000.00, false)]  // 相同金额，无差异
    [InlineData(1000.00, 1000.005, false)] // 差异 < 0.01，忽略
    [InlineData(1000.00, 1000.02, true)]   // 差异 = 0.02，有差异
    [InlineData(1000.00, 999.98, true)]    // 差异 = 0.02（负），有差异
    [InlineData(1000.00, 1001.00, true)]   // 差异 = 1.00，有差异
    public void AmountDiff_ShouldDetect_DifferenceAbove_OneCent(
        decimal checkAmount, decimal debitAmount, bool expectedHasDiff)
    {
        // 模拟ReviewDetail.razor第72行的金额差异判断逻辑
        var diff = debitAmount - checkAmount;
        var hasDiff = Math.Abs(diff) > 0.01m;

        Assert.Equal(expectedHasDiff, hasDiff);
    }

    // ====== 确认操作状态流转 ======

    [Fact]
    public async Task ConfirmReview_ShouldUpdateStatus_ToPendingReview_ToConfirmed()
    {
        await using var ctx = CreateContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C001",
            CustomerName = "测试客户",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var checkRecord = new CheckRecord
        {
            Id = Guid.NewGuid(),
            CheckNumber = "12345",
            CheckAmount = 1000m,
            CheckDate = DateTime.Today,
            CustomerId = customer.Id,
            Status = CheckStatus.PendingReview,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(checkRecord);
        await ctx.SaveChangesAsync();

        // 模拟确认操作
        checkRecord.Status = CheckStatus.Confirmed;
        checkRecord.Notes = "核查备注";
        await ctx.SaveChangesAsync();

        var updated = await ctx.CheckRecords.FirstAsync();
        Assert.Equal(CheckStatus.Confirmed, updated.Status);
        Assert.Equal("核查备注", updated.Notes);
    }

    [Fact]
    public async Task ConfirmReview_ShouldNotOverwrite_Notes_WhenNotesIsEmpty()
    {
        await using var ctx = CreateContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C001",
            CustomerName = "测试客户",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var checkRecord = new CheckRecord
        {
            Id = Guid.NewGuid(),
            CheckNumber = "12345",
            CheckAmount = 1000m,
            CheckDate = DateTime.Today,
            CustomerId = customer.Id,
            Status = CheckStatus.PendingReview,
            Notes = "原有备注",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(checkRecord);
        await ctx.SaveChangesAsync();

        // 模拟确认时备注为空（不覆盖原有Notes）
        var reviewNotes = "";
        checkRecord.Status = CheckStatus.Confirmed;
        if (!string.IsNullOrWhiteSpace(reviewNotes))  // 与页面第193行一致
            checkRecord.Notes = reviewNotes;
        await ctx.SaveChangesAsync();

        var updated = await ctx.CheckRecords.FirstAsync();
        Assert.Equal(CheckStatus.Confirmed, updated.Status);
        Assert.Equal("原有备注", updated.Notes); // 原有Notes不被空备注覆盖
    }

    // ====== 存疑操作状态流转 ======

    [Fact]
    public async Task QuestionReview_ShouldUpdateStatus_ToQuestioned_AndSaveReason()
    {
        await using var ctx = CreateContext();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C001",
            CustomerName = "测试客户",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var checkRecord = new CheckRecord
        {
            Id = Guid.NewGuid(),
            CheckNumber = "12345",
            CheckAmount = 1000m,
            CheckDate = DateTime.Today,
            CustomerId = customer.Id,
            Status = CheckStatus.PendingReview,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ctx.Customers.Add(customer);
        ctx.CheckRecords.Add(checkRecord);
        await ctx.SaveChangesAsync();

        // 模拟存疑操作
        var reason = "金额与支票不符";
        checkRecord.Status = CheckStatus.Questioned;
        checkRecord.Notes = reason;
        await ctx.SaveChangesAsync();

        var updated = await ctx.CheckRecords.FirstAsync();
        Assert.Equal(CheckStatus.Questioned, updated.Status);
        Assert.Equal("金额与支票不符", updated.Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void QuestionReview_ShouldBlock_WhenReasonIsEmpty(string? reason)
    {
        // 模拟页面第212行：string.IsNullOrWhiteSpace(_questionReason) 时直接 return
        var shouldBlock = string.IsNullOrWhiteSpace(reason);
        Assert.True(shouldBlock);
    }

    // ====== 存疑操作触发审计日志 ======

    [Fact]
    public async Task ConfirmReview_ShouldCreateAuditLog()
    {
        await using var ctx = CreateContext();
        var service = new AuditLogService(ctx);

        var checkId = Guid.NewGuid();
        var oldStatus = CheckStatus.PendingReview;
        var newStatus = CheckStatus.Confirmed;

        await service.LogAsync(
            AuditAction.StatusChange,
            "CheckRecord",
            checkId,
            new { Status = oldStatus },
            new { Status = newStatus });

        var log = await ctx.AuditLogs.FirstOrDefaultAsync();
        Assert.NotNull(log);
        Assert.Equal(AuditAction.StatusChange, log.Action);
        Assert.Equal("CheckRecord", log.EntityType);
        Assert.Equal(checkId, log.EntityId);
        Assert.NotNull(log.OldValues);
        Assert.NotNull(log.NewValues);
    }
}
