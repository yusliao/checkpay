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
        var userId = await SeedUserAsync(context, "admin", "admin@checkpay.local");
        var service = new AuditLogService(context);

        var entityId = Guid.NewGuid();
        await service.LogAsync(AuditAction.Create, "CheckRecord", entityId, null, new { Amount = 100 });

        var log = await context.AuditLogs.FirstOrDefaultAsync();
        Assert.NotNull(log);
        Assert.Equal(AuditAction.Create, log.Action);
        Assert.Equal("CheckRecord", log.EntityType);
        Assert.Equal(entityId, log.EntityId);
        Assert.Equal(userId, log.UserId);
        Assert.NotNull(log.NewValues);
    }

    [Fact]
    public async Task LogAsync_ShouldHandleNullValues()
    {
        await using var context = CreateContext();
        var userId = await SeedUserAsync(context, "admin", "admin@checkpay.local");
        var service = new AuditLogService(context);

        var entityId = Guid.NewGuid();
        await service.LogAsync(AuditAction.Delete, "CheckRecord", entityId);

        var log = await context.AuditLogs.FirstOrDefaultAsync();
        Assert.NotNull(log);
        Assert.Equal(userId, log.UserId);
        Assert.Null(log.OldValues);
        Assert.Null(log.NewValues);
    }

    [Fact]
    public async Task LogAsync_ShouldFallbackToAdmin_WhenClaimMissing()
    {
        await using var context = CreateContext();
        var adminId = await SeedUserAsync(context, "admin", "admin@checkpay.local");
        var service = new AuditLogService(context);

        await service.LogAsync(AuditAction.Update, "CheckRecord", Guid.NewGuid());

        var log = await context.AuditLogs.FirstOrDefaultAsync();
        Assert.NotNull(log);
        Assert.Equal(adminId, log.UserId);
    }

    private static async Task<Guid> SeedUserAsync(ApplicationDbContext context, string entraId, string email)
    {
        var userId = Guid.NewGuid();
        context.Users.Add(new CheckPay.Domain.Entities.User
        {
            Id = userId,
            EntraId = entraId,
            Email = email,
            DisplayName = "Test User",
            PasswordHash = "hashed",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return userId;
    }
}
