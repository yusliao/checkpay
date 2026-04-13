using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class CheckOcrFewShotProviderTests
{
    [Fact]
    public async Task BuildAugmentation_ShouldIncludeModelAndAuditedJson_WhenCheckSamplesExist()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);

        var achOcr = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "064000017", "12345678", null, null, null, null, null, null, "MICR…", "9999", null));
        var achOk = CheckAchExtensionData.Serialize(new CheckAchExtensionData(
            "064000017", "12345678", null, null, null, null, null, null, "MICR…", "8888", null));

        db.OcrTrainingSamples.Add(new OcrTrainingSample
        {
            ImageUrl = "http://minio/x/1.jpg",
            DocumentType = "check",
            OcrRawResponse = "{}",
            OcrCheckNumber = "1111",
            CorrectCheckNumber = "2222",
            OcrAmount = 10m,
            CorrectAmount = 20m,
            OcrAchExtensionJson = achOcr,
            CorrectAchExtensionJson = achOk
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:CheckFewShotMaxSamples"] = "5",
            ["Ocr:CheckFewShotMaxChars"] = "50000"
        }).Build();

        var provider = new CheckOcrFewShotProvider(db, config, NullLogger<CheckOcrFewShotProvider>.Instance);
        var text = await provider.BuildCheckPromptAugmentationAsync();

        Assert.Contains("model_extracted", text);
        Assert.Contains("audited_ground_truth", text);
        Assert.Contains("1111", text);
        Assert.Contains("2222", text);
        Assert.Contains("8888", text);
    }

    [Fact]
    public async Task BuildAugmentation_ShouldReturnEmpty_WhenMaxSamplesIsZero()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        db.OcrTrainingSamples.Add(new OcrTrainingSample
        {
            ImageUrl = "http://minio/x/1.jpg",
            DocumentType = "check",
            OcrRawResponse = "{}"
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:CheckFewShotMaxSamples"] = "0"
        }).Build();

        var provider = new CheckOcrFewShotProvider(db, config, NullLogger<CheckOcrFewShotProvider>.Instance);
        var text = await provider.BuildCheckPromptAugmentationAsync();

        Assert.Equal(string.Empty, text);
    }
}
