using CheckPay.Domain.Enums;

namespace CheckPay.Application.Common.Interfaces;

public interface IAuditLogService
{
    Task LogAsync(AuditAction action, string entityType, Guid entityId, object? oldValues = null, object? newValues = null);
}
