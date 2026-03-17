using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Infrastructure;

public class AuditLogServiceTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task LogAsync_ShouldCreateAuditLog()
    {
        await using var context = CreateContext();
        var service = new AuditLogService(context);

        var entityId = Guid.NewGuid();
        await service.LogAsync(AuditAction.Create, "CheckRecord", entityId, null, new { Amount = 100 });

        var log = await context.AuditLogs.FirstOrDefaultAsync();
        Assert.NotNull(log);
        Assert.Equal(AuditAction.Create, log.Action);
        Assert.Equal("CheckRecord", log.EntityType);
        Assert.Equal(entityId, log.EntityId);
        Assert.NotNull(log.NewValues);
    }

    [Fact]
    public async Task LogAsync_ShouldHandleNullValues()
    {
        await using var context = CreateContext();
        var service = new AuditLogService(context);

        var entityId = Guid.NewGuid();
        await service.LogAsync(AuditAction.Delete, "CheckRecord", entityId);

        var log = await context.AuditLogs.FirstOrDefaultAsync();
        Assert.NotNull(log);
        Assert.Null(log.OldValues);
        Assert.Null(log.NewValues);
    }
}
