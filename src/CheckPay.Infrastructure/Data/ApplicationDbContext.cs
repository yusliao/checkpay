using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Infrastructure.Data;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<CheckRecord> CheckRecords => Set<CheckRecord>();
    public DbSet<DebitRecord> DebitRecords => Set<DebitRecord>();
    public DbSet<OcrResult> OcrResults => Set<OcrResult>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // 全局软删除过滤器
        modelBuilder.Entity<CheckRecord>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<DebitRecord>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<Customer>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<User>().HasQueryFilter(e => e.DeletedAt == null);
    }
}
