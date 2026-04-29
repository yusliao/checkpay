using CheckPay.Domain.Entities;
using CheckPay.Infrastructure.Data;
using CheckPay.Web.Common;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Web;

public class CheckNumberGuardTests
{
    [Fact]
    public void Normalize_TrimAndUppercase_ReturnsNormalizedValue()
    {
        var normalized = CheckNumberGuard.Normalize("  ab-123  ");

        Assert.Equal("AB-123", normalized);
    }

    [Fact]
    public async Task ExistsAsync_MatchesDifferentCaseAndWhitespace_ReturnsTrue()
    {
        await using var db = CreateDbContext(nameof(ExistsAsync_MatchesDifferentCaseAndWhitespace_ReturnsTrue));
        var customer = CreateCustomer();
        db.Customers.Add(customer);
        db.CheckRecords.Add(CreateCheckRecord(customer.Id, "  ab-123  ", routingNumber: "123456789"));
        await db.SaveChangesAsync();

        var exists = await CheckNumberGuard.ExistsAsync(db, "AB-123", "123456789");

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_IncludesSoftDeletedRecords_ReturnsTrue()
    {
        await using var db = CreateDbContext(nameof(ExistsAsync_IncludesSoftDeletedRecords_ReturnsTrue));
        var customer = CreateCustomer();
        db.Customers.Add(customer);
        db.CheckRecords.Add(CreateCheckRecord(customer.Id, "CHK-001", routingNumber: "111111111", deleted: true));
        await db.SaveChangesAsync();

        var exists = await CheckNumberGuard.ExistsAsync(db, " chk-001 ", "111111111");

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_ExcludeSelf_ReturnsFalse()
    {
        await using var db = CreateDbContext(nameof(ExistsAsync_ExcludeSelf_ReturnsFalse));
        var customer = CreateCustomer();
        db.Customers.Add(customer);
        var check = CreateCheckRecord(customer.Id, "CHK-SELF", routingNumber: "222222222");
        db.CheckRecords.Add(check);
        await db.SaveChangesAsync();

        var exists = await CheckNumberGuard.ExistsAsync(db, "chk-self", "222222222", check.Id);

        Assert.False(exists);
    }

    [Fact]
    public async Task ExistsAsync_DifferentRoutingNumber_ReturnsFalse()
    {
        await using var db = CreateDbContext(nameof(ExistsAsync_DifferentRoutingNumber_ReturnsFalse));
        var customer = CreateCustomer();
        db.Customers.Add(customer);
        db.CheckRecords.Add(CreateCheckRecord(customer.Id, "CHK-888", routingNumber: "333333333"));
        await db.SaveChangesAsync();

        var exists = await CheckNumberGuard.ExistsAsync(db, "chk-888", "444444444");

        Assert.False(exists);
    }

    [Fact]
    public void IsUniqueViolation_WhenConstraintNameInInnerException_ReturnsTrue()
    {
        var ex = new DbUpdateException(
            "save failed",
            new Exception("duplicate key value violates unique constraint \"ix_check_records_check_number\""));

        var isUniqueViolation = CheckNumberGuard.IsUniqueViolation(ex);

        Assert.True(isUniqueViolation);
    }

    [Fact]
    public void IsUniqueViolation_WhenOtherError_ReturnsFalse()
    {
        var ex = new DbUpdateException("save failed", new Exception("timeout"));

        var isUniqueViolation = CheckNumberGuard.IsUniqueViolation(ex);

        Assert.False(isUniqueViolation);
    }

    private static ApplicationDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static Customer CreateCustomer()
        => new()
        {
            CustomerCode = Guid.NewGuid().ToString("N"),
            CustomerName = "test",
            MobilePhone = "13800000000",
            IsActive = true,
            IsAuthorized = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static CheckRecord CreateCheckRecord(
        Guid customerId,
        string checkNumber,
        string? routingNumber,
        bool deleted = false)
        => new()
        {
            CheckNumber = checkNumber,
            RoutingNumber = routingNumber,
            CheckAmount = 100m,
            CheckDate = DateTime.UtcNow.Date,
            CustomerId = customerId,
            Status = CheckPay.Domain.Enums.CheckStatus.PendingDebit,
            CustomerMasterMismatchWarning = false,
            CustomerCompanyNewRelationshipWarning = false,
            AchDebitSucceeded = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null
        };
}
