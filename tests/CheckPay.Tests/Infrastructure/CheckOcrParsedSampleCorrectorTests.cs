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

    /// <summary>
    /// 同一路由下多张票可能共享相同的误解析支票号（实为账号）；MICR 数字指纹不同则不得强匹配纠偏。
    /// </summary>
    [Fact]
    public async Task ApplyIfMatched_ShouldNotStrongMatch_WhenSameCheckNumberRouting_ButMicrDigitFingerprintDiffers()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var ocrAchSample = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "267084131",
            "710978795",
            null,
            null,
            "addr ocr",
            null,
            null,
            null,
            "⑈001128⑈ ⑆267084131⑆\n710978795⑈",
            null,
            null,
            null));
        var okAchSample = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "267084131",
            "710978795",
            null,
            null,
            "addr ok",
            null,
            null,
            null,
            "⑈001128⑈ ⑆267084131⑆\n710978795⑈",
            null,
            null,
            null));

        db.OcrTrainingSamples.Add(new OcrTrainingSample
        {
            ImageUrl = "http://x/20.jpg",
            DocumentType = "check",
            OcrRawResponse = "{}",
            OcrCheckNumber = "710978795",
            CorrectCheckNumber = "710978795",
            OcrAmount = 5158.87m,
            CorrectAmount = 5158.87m,
            OcrDate = new DateTime(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc),
            CorrectDate = new DateTime(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc),
            OcrAchExtensionJson = ocrAchSample,
            CorrectAchExtensionJson = okAchSample
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
            CheckNumber: "710978795",
            Amount: 7403.32m,
            Date: new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
            ConfidenceScores: new Dictionary<string, double> { ["Amount"] = 0.42 },
            RoutingNumber: "267084131",
            AccountNumber: "710978795",
            AccountAddress: "addr from 21",
            MicrLineRaw: "⑈001078⑈ ⑆267084131⑆\n710978795⑈");

        var result = await corrector.ApplyIfMatchedAsync(parsed);

        Assert.Same(parsed, result);
        Assert.Equal(7403.32m, result.Amount);
        Assert.Equal("addr from 21", result.AccountAddress);
    }
}
