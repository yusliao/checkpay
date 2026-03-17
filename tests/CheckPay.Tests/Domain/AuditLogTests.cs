using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

namespace CheckPay.Tests.Domain;

public class AuditLogTests
{
    [Fact]
    public void AuditLog_ShouldInitializeWithDefaultValues()
    {
        var auditLog = new AuditLog();

        Assert.Equal(Guid.Empty, auditLog.UserId);
        Assert.Equal(string.Empty, auditLog.EntityType);
        Assert.Equal(Guid.Empty, auditLog.EntityId);
        Assert.Null(auditLog.OldValues);
        Assert.Null(auditLog.NewValues);
    }

    [Fact]
    public void AuditLog_ShouldSetProperties()
    {
        var userId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        var auditLog = new AuditLog
        {
            UserId = userId,
            Action = AuditAction.Update,
            EntityType = "CheckRecord",
            EntityId = entityId
        };

        Assert.Equal(userId, auditLog.UserId);
        Assert.Equal(AuditAction.Update, auditLog.Action);
        Assert.Equal("CheckRecord", auditLog.EntityType);
        Assert.Equal(entityId, auditLog.EntityId);
    }
}
