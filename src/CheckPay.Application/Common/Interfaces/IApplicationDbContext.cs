using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<CheckRecord> CheckRecords { get; }
    DbSet<DebitRecord> DebitRecords { get; }
    DbSet<OcrResult> OcrResults { get; }
    DbSet<Customer> Customers { get; }
    DbSet<User> Users { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
