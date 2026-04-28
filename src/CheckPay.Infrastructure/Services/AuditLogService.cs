using System.Text.Json;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CheckPay.Infrastructure.Services;

public class AuditLogService : IAuditLogService
{
    private readonly IApplicationDbContext _context;

    public AuditLogService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task LogAsync(AuditAction action, string entityType, Guid entityId, object? oldValues = null, object? newValues = null)
    {
        var actorUserId = await ResolveActorUserIdAsync();
        if (!actorUserId.HasValue)
            return;

        var log = new AuditLog
        {
            UserId = actorUserId.Value,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues != null ? JsonDocument.Parse(JsonSerializer.Serialize(oldValues)) : null,
            NewValues = newValues != null ? JsonDocument.Parse(JsonSerializer.Serialize(newValues)) : null
        };

        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    private async Task<Guid?> ResolveActorUserIdAsync()
    {
        var claimValue = (ClaimsPrincipal.Current ?? Thread.CurrentPrincipal as ClaimsPrincipal)
            ?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(claimValue, out var claimedUserId))
        {
            var exists = await _context.Users.AsNoTracking().AnyAsync(u => u.Id == claimedUserId);
            if (exists)
                return claimedUserId;
        }

        var adminId = await _context.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.EntraId == "admin" || u.Email == "admin@checkpay.local"))
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync();
        if (adminId.HasValue)
            return adminId.Value;

        return await _context.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.CreatedAt)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync();
    }
}
