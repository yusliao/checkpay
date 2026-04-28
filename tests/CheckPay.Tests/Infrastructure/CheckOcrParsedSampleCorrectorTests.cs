using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class CheckOcrParsedSampleCorrectorTests
{
    [Fact]
    public async Task ApplyIfMatched_ShouldReplaceWithCorrectFields_WhenRoutingAndOcrCheckMatch()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var ocrAch = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "064000017", "111", null, null, null, null, null, null, null, "1001", null));
        var okAch = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "064000017", "222", "OK BANK", null, null, null, null, null, null, "1002", null));

        db.OcrTrainingSamples.Add(new OcrTrainingSample
        {
            ImageUrl = "http://x/1.jpg",
            DocumentType = "check",
            OcrRawResponse = "{}",
            OcrCheckNumber = "5001",
            CorrectCheckNumber = "5002",
            OcrAmount = 10m,
            CorrectAmount = 10m,
            OcrDate = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            CorrectDate = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            OcrAchExtensionJson = ocrAch,
            CorrectAchExtensionJson = okAch
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:CheckAzureTrainingCorrectionEnabled"] = "true",
            ["Ocr:CheckAzureTrainingCorrectionMaxScan"] = "40",
            ["Ocr:CheckAzureTrainingCorrectionClusterMinSamples"] = "1",
            ["Ocr:CheckAzureTrainingCorrectionSampleMinAgeMinutes"] = "0"
        }).Build();

        var corrector = new CheckOcrParsedSampleCorrector(db, config, NullLogger<CheckOcrParsedSampleCorrector>.Instance);

        var parsed = new OcrResultDto(
            CheckNumber: "5001",
            Amount: 10m,
            Date: new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            ConfidenceScores: new Dictionary<string, double> { ["CheckNumber"] = 0.5 },
            RoutingNumber: "064000017",
            AccountNumber: "111",
            CheckNumberMicr: "1001");

        var result = await corrector.ApplyIfMatchedAsync(parsed);

        Assert.Equal("5002", result.CheckNumber);
        Assert.Equal("222", result.AccountNumber);
        Assert.Equal("1002", result.CheckNumberMicr);
        Assert.Equal("OK BANK", result.BankName);
    }

    [Fact]
    public async Task ApplyIfMatched_ShouldReturnOriginal_WhenDisabled()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        db.OcrTrainingSamples.Add(new OcrTrainingSample
        {
            ImageUrl = "http://x/1.jpg",
            DocumentType = "check",
            OcrRawResponse = "{}",
            OcrCheckNumber = "1",
            CorrectCheckNumber = "2"
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:CheckAzureTrainingCorrectionEnabled"] = "false"
        }).Build();

        var corrector = new CheckOcrParsedSampleCorrector(db, config, NullLogger<CheckOcrParsedSampleCorrector>.Instance);
        var parsed = new OcrResultDto(
            "1", 1m, DateTime.UtcNow, new Dictionary<string, double> { ["CheckNumber"] = 0.5 });

        var result = await corrector.ApplyIfMatchedAsync(parsed);
        Assert.Same(parsed, result);
    }

    [Fact]
    public async Task ApplyIfMatched_ShouldSkip_WhenClusterSampleCountBelowThreshold()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var ocrAch = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "064000017", "111", null, null, null, null, null, null, null, "1001", null));
        var okAch = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "064000017", "222", "OK BANK", null, null, null, null, null, null, "1002", null));

        db.OcrTrainingSamples.Add(new OcrTrainingSample
        {
            ImageUrl = "http://x/1.jpg",
            DocumentType = "check",
            OcrRawResponse = "{}",
            OcrCheckNumber = "5001",
            CorrectCheckNumber = "5002",
            OcrAmount = 10m,
            CorrectAmount = 10m,
            OcrDate = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            CorrectDate = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            OcrAchExtensionJson = ocrAch,
            CorrectAchExtensionJson = okAch
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:CheckAzureTrainingCorrectionEnabled"] = "true",
            ["Ocr:CheckAzureTrainingCorrectionClusterMinSamples"] = "2",
            ["Ocr:CheckAzureTrainingCorrectionSampleMinAgeMinutes"] = "0"
        }).Build();

        var corrector = new CheckOcrParsedSampleCorrector(db, config, NullLogger<CheckOcrParsedSampleCorrector>.Instance);
        var parsed = new OcrResultDto(
            CheckNumber: "5001",
            Amount: 10m,
            Date: new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            ConfidenceScores: new Dictionary<string, double> { ["CheckNumber"] = 0.5 },
            RoutingNumber: "064000017",
            AccountNumber: "111",
            CheckNumberMicr: "1001");

        var result = await corrector.ApplyIfMatchedAsync(parsed);
        Assert.Same(parsed, result);
    }
}
