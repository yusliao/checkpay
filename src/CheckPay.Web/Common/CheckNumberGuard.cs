using CheckPay.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Web.Common;

public static class CheckNumberGuard
{
    private const string UniqueConstraintName = "ix_check_records_check_number";

    public static string Normalize(string? checkNumber)
        => string.IsNullOrWhiteSpace(checkNumber)
            ? string.Empty
            : checkNumber.Trim().ToUpperInvariant();

    public static string NormalizeRouting(string? routingNumber)
        => string.IsNullOrWhiteSpace(routingNumber)
            ? string.Empty
            : routingNumber.Trim();

    public static async Task<bool> ExistsAsync(
        IApplicationDbContext dbContext,
        string? checkNumber,
        string? routingNumber,
        Guid? excludeCheckRecordId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCheckNumber = Normalize(checkNumber);
        if (string.IsNullOrEmpty(normalizedCheckNumber))
            return false;

        var normalizedRouting = NormalizeRouting(routingNumber);
        var excludedId = excludeCheckRecordId ?? Guid.Empty;

        return await dbContext.CheckRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                c => c.Id != excludedId
                     && c.CheckNumber.Trim().ToUpper() == normalizedCheckNumber
                     && (c.RoutingNumber ?? string.Empty).Trim() == normalizedRouting,
                cancellationToken);
    }

    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        Exception? current = exception;
        while (current != null)
        {
            var message = current.Message;
            if (!string.IsNullOrWhiteSpace(message) &&
                message.Contains(UniqueConstraintName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}
