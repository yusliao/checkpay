using System.Text.Json;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

namespace CheckPay.Infrastructure.Services;

public class AuditLogService : IAuditLogService
{
    private readonly IApplicationDbContext _context;
    private static readonly Guid SystemUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public AuditLogService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task LogAsync(AuditAction action, string entityType, Guid entityId, object? oldValues = null, object? newValues = null)
    {
        var log = new AuditLog
        {
            UserId = SystemUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues != null ? JsonDocument.Parse(JsonSerializer.Serialize(oldValues)) : null,
            NewValues = newValues != null ? JsonDocument.Parse(JsonSerializer.Serialize(newValues)) : null
        };

        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();
    }
}
