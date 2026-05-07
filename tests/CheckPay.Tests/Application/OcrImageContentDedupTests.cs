using System.Text.Json;
using CheckPay.Application.Common;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Application;

public class OcrImageContentDedupTests
{
    [Fact]
    public void ComputeSha256Hex_SameBytes_SameHex()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var a = OcrImageContentDedup.ComputeSha256Hex(data);
        var b = OcrImageContentDedup.ComputeSha256Hex(data.AsSpan());
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[a-f0-9]{64}$", a);
    }

    [Fact]
    public void CopyCompletedOcrFromSource_ClonesJsonAndClearsAzure()
    {
        using var raw = JsonDocument.Parse("""{"k":1}""");
        using var conf = JsonDocument.Parse("""{"Amount":0.9}""");
        using var amt = JsonDocument.Parse("""{"status":"skipped"}""");

        var source = new OcrResult
        {
            RawResult = raw,
            ConfidenceScores = conf,
            AmountValidationResult = amt,
            AmountValidationStatus = AmountValidationStatus.Skipped,
            AmountValidationErrorMessage = "x",
            AmountValidatedAt = DateTime.UtcNow,
            AzureRawResult = JsonDocument.Parse("""{"old":true}"""),
            AzureStatus = OcrStatus.Completed
        };

        var target = new OcrResult { ImageUrl = "http://new", ImageContentSha256 = "abc" };
        OcrImageContentDedup.CopyCompletedOcrFromSource(target, source);

        Assert.Equal(OcrStatus.Completed, target.Status);
        Assert.NotNull(target.RawResult);
        Assert.Equal(1, target.RawResult.RootElement.GetProperty("k").GetInt32());
        Assert.NotNull(target.ConfidenceScores);
        Assert.Equal(0.9, target.ConfidenceScores.RootElement.GetProperty("Amount").GetDouble());
        Assert.NotNull(target.AmountValidationResult);
        Assert.Equal(AmountValidationStatus.Skipped, target.AmountValidationStatus);
        Assert.Null(target.AzureRawResult);
        Assert.Equal(OcrStatus.Pending, target.AzureStatus);

        target.RawResult?.Dispose();
        target.ConfidenceScores?.Dispose();
        target.AmountValidationResult?.Dispose();
        source.AzureRawResult?.Dispose();
    }

    [Fact]
    public async Task FindReusableCompletedAsync_WithoutCheckRecord_ReturnsNull()
    {
        var hash = OcrImageContentDedup.ComputeSha256Hex(new byte[] { 9, 8, 7 });
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        db.OcrResults.Add(new OcrResult
        {
            ImageUrl = "http://minio/only-ocr",
            ImageContentSha256 = hash,
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse("""{"k":1}"""),
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var found = await OcrImageContentDedup.FindReusableCompletedAsync(db, hash, null);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindReusableCompletedAsync_WithDraftCheckLinkingToOcrSameHash_ReturnsNewestCompletedSource()
    {
        var hash = OcrImageContentDedup.ComputeSha256Hex(new byte[] { 1, 2, 3, 4 });
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var older = new OcrResult
        {
            ImageUrl = "http://minio/a",
            ImageContentSha256 = hash,
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse("""{"v":"older"}"""),
            CreatedAt = DateTime.UtcNow.AddHours(-3),
            UpdatedAt = DateTime.UtcNow.AddHours(-3)
        };
        db.OcrResults.Add(older);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            CustomerCode = "c-" + Guid.NewGuid().ToString("N")[..8],
            CustomerName = "t",
            MobilePhone = "10000000001",
            IsActive = true,
            IsAuthorized = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        db.CheckRecords.Add(new CheckRecord
        {
            CheckNumber = "CHK-DEDUP",
            CheckAmount = 10m,
            CheckDate = DateTime.UtcNow.Date,
            CustomerId = customer.Id,
            Status = CheckStatus.PendingDebit,
            OcrResultId = older.Id,
            SubmittedAt = null,
            CustomerMasterMismatchWarning = false,
            CustomerCompanyNewRelationshipWarning = false,
            AchDebitSucceeded = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var newer = new OcrResult
        {
            ImageUrl = "http://minio/b",
            ImageContentSha256 = hash,
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse("""{"v":"newer"}"""),
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.OcrResults.Add(newer);
        await db.SaveChangesAsync();

        var found = await OcrImageContentDedup.FindReusableCompletedAsync(db, hash, null);

        Assert.NotNull(found);
        Assert.Equal(newer.Id, found.Id);
        Assert.Equal("newer", found.RawResult!.RootElement.GetProperty("v").GetString());
    }

    [Fact]
    public async Task FindReusableCompletedAsync_SoftDeletedCheckRecord_ReturnsNull()
    {
        var hash = OcrImageContentDedup.ComputeSha256Hex(new byte[] { 5, 5, 5 });
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var ocr = new OcrResult
        {
            ImageUrl = "http://minio/x",
            ImageContentSha256 = hash,
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse("""{}"""),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.OcrResults.Add(ocr);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            CustomerCode = "c-" + Guid.NewGuid().ToString("N")[..8],
            CustomerName = "t",
            MobilePhone = "10000000002",
            IsActive = true,
            IsAuthorized = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        db.CheckRecords.Add(new CheckRecord
        {
            CheckNumber = "CHK-DEL",
            CheckAmount = 1m,
            CheckDate = DateTime.UtcNow.Date,
            CustomerId = customer.Id,
            Status = CheckStatus.PendingDebit,
            OcrResultId = ocr.Id,
            CustomerMasterMismatchWarning = false,
            CustomerCompanyNewRelationshipWarning = false,
            AchDebitSucceeded = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var found = await OcrImageContentDedup.FindReusableCompletedAsync(db, hash, null);

        Assert.Null(found);
    }
}
