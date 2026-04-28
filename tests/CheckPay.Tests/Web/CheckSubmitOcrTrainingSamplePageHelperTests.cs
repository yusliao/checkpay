using System.Text.Json;
using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using CheckPay.Web.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CheckPay.Tests.Web;

public class CheckSubmitOcrTrainingSamplePageHelperTests
{
    private sealed class NullTemplateResolver : ICheckOcrTemplateResolver
    {
        public Task<OcrTemplateResolution> ResolveAsync(
            string? routingDigits9,
            string extractedFullText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrTemplateResolution(null, null, CheckOcrParsingProfile.Default));
    }

    private sealed class FixedTemplateResolver(Guid templateId) : ICheckOcrTemplateResolver
    {
        public Task<OcrTemplateResolution> ResolveAsync(
            string? routingDigits9,
            string extractedFullText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrTemplateResolution(templateId, "fixture", CheckOcrParsingProfile.Default));
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => (string?)p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    [Fact]
    public async Task TryAppend_WhenDisabled_DoesNotInsert()
    {
        await using var db = CreateContext();
        var ocrId = Guid.NewGuid();
        var dto = new OcrResultDto(
            "A",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>());
        db.OcrResults.Add(new OcrResult
        {
            Id = ocrId,
            ImageUrl = "http://x/a.jpg",
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse(JsonSerializer.Serialize(dto))
        });
        await db.SaveChangesAsync();

        var cfg = Config(("Ocr:Training:AutoSampleOnCheckSubmit", "false"));
        await CheckSubmitOcrTrainingSamplePageHelper.TryAppendAfterCheckFinalSubmitAsync(
            db,
            cfg,
            "http://x/a.jpg",
            ocrId,
            "B",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckAchExtensionData.FromOcrResult(dto with { CheckNumber = "B" }),
            Guid.NewGuid(),
            new NullTemplateResolver(),
            NullLogger.Instance);

        Assert.Equal(0, await db.OcrTrainingSamples.CountAsync());
    }

    [Fact]
    public async Task TryAppend_WhenDiff_AppendsThenDedupSkipsSecond()
    {
        await using var db = CreateContext();
        var ocrId = Guid.NewGuid();
        var checkId = Guid.NewGuid();
        var dto = new OcrResultDto(
            "A",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>());
        db.OcrResults.Add(new OcrResult
        {
            Id = ocrId,
            ImageUrl = "http://x/a.jpg",
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse(JsonSerializer.Serialize(dto))
        });
        await db.SaveChangesAsync();

        var cfg = Config(
            ("Ocr:Training:AutoSampleOnCheckSubmit", "true"),
            ("Ocr:Training:AutoSampleRequireDiff", "true"),
            ("Ocr:Training:AutoSampleDedupByOcrResultId", "true"));

        var submitted = CheckAchExtensionData.FromOcrResult(dto with { CheckNumber = "B" });

        await CheckSubmitOcrTrainingSamplePageHelper.TryAppendAfterCheckFinalSubmitAsync(
            db,
            cfg,
            "http://x/a.jpg",
            ocrId,
            "B",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            submitted,
            checkId,
            new NullTemplateResolver(),
            NullLogger.Instance);

        Assert.Equal(1, await db.OcrTrainingSamples.CountAsync());

        await CheckSubmitOcrTrainingSamplePageHelper.TryAppendAfterCheckFinalSubmitAsync(
            db,
            cfg,
            "http://x/a.jpg",
            ocrId,
            "B",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            submitted,
            Guid.NewGuid(),
            new NullTemplateResolver(),
            NullLogger.Instance);

        Assert.Equal(1, await db.OcrTrainingSamples.CountAsync());
    }

    [Fact]
    public async Task TryAppend_ResolvesTemplateId_OnSample()
    {
        await using var db = CreateContext();
        var ocrId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var dto = new OcrResultDto(
            "A",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>(),
            ExtractedText: "fixture bank text",
            RoutingNumber: "021000021");
        db.OcrResults.Add(new OcrResult
        {
            Id = ocrId,
            ImageUrl = "http://x/a.jpg",
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse(JsonSerializer.Serialize(dto))
        });
        await db.SaveChangesAsync();

        var cfg = Config(
            ("Ocr:Training:AutoSampleOnCheckSubmit", "true"),
            ("Ocr:Training:AutoSampleRequireDiff", "true"),
            ("Ocr:Training:AutoSampleDedupByOcrResultId", "false"));

        var submitted = CheckAchExtensionData.FromOcrResult(dto with { CheckNumber = "B" });

        await CheckSubmitOcrTrainingSamplePageHelper.TryAppendAfterCheckFinalSubmitAsync(
            db,
            cfg,
            "http://x/a.jpg",
            ocrId,
            "B",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            submitted,
            Guid.NewGuid(),
            new FixedTemplateResolver(templateId),
            NullLogger.Instance);

        var row = await db.OcrTrainingSamples.AsNoTracking().SingleAsync();
        Assert.Equal(templateId, row.OcrCheckTemplateId);
        Assert.Contains("checkRecordId=", row.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryAppend_WhenOnlyHighConfidenceFieldChanged_SkipsAutoSample()
    {
        await using var db = CreateContext();
        var ocrId = Guid.NewGuid();
        var dto = new OcrResultDto(
            "A100",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>());
        db.OcrResults.Add(new OcrResult
        {
            Id = ocrId,
            ImageUrl = "http://x/a.jpg",
            Status = OcrStatus.Completed,
            RawResult = JsonDocument.Parse(JsonSerializer.Serialize(dto)),
            ConfidenceScores = JsonDocument.Parse("""{"CheckNumber":0.95,"Amount":0.90,"Date":0.90}""")
        });
        await db.SaveChangesAsync();

        var cfg = Config(
            ("Ocr:Training:AutoSampleOnCheckSubmit", "true"),
            ("Ocr:Training:AutoSampleRequireDiff", "true"),
            ("Ocr:Training:AutoSampleDedupByOcrResultId", "false"),
            ("Ocr:Training:AutoSampleLogVerbosity", "Verbose"));

        await CheckSubmitOcrTrainingSamplePageHelper.TryAppendAfterCheckFinalSubmitAsync(
            db,
            cfg,
            "http://x/a.jpg",
            ocrId,
            "A200",
            1m,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckAchExtensionData.FromOcrResult(dto with { CheckNumber = "A200" }),
            Guid.NewGuid(),
            new NullTemplateResolver(),
            NullLogger.Instance);

        Assert.Equal(0, await db.OcrTrainingSamples.CountAsync());
    }
}
