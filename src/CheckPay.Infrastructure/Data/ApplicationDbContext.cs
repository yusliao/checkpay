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
    public DbSet<CustomerCompanyName> CustomerCompanyNames => Set<CustomerCompanyName>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OcrTrainingSample> OcrTrainingSamples => Set<OcrTrainingSample>();
    public DbSet<OcrCheckTemplate> OcrCheckTemplates => Set<OcrCheckTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // 全局软删除过滤器（AuditLog 与 User 为必填关系：需与 User 过滤器对齐，避免 EF 10622）
        modelBuilder.Entity<CheckRecord>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<DebitRecord>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<Customer>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<User>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<CustomerCompanyName>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<AuditLog>().HasQueryFilter(a => a.User.DeletedAt == null);
    }
}
