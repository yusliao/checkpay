using System.Text.Json;
using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Xunit;

namespace CheckPay.Tests.Application;

public class SubmitCheckOcrTrainingSampleFactoryTests
{
    private static readonly Guid FixtureOcrResultId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FixtureCheckRecordId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void BuildAutoSubmitNotes_ContainsIds()
    {
        var n = SubmitCheckOcrTrainingSampleFactory.BuildAutoSubmitNotes(FixtureOcrResultId, FixtureCheckRecordId);
        Assert.StartsWith(SubmitCheckOcrTrainingSampleFactory.AutoSubmitNotesPrefix, n, StringComparison.Ordinal);
        Assert.Contains($"ocrResultId={FixtureOcrResultId:D}", n, StringComparison.Ordinal);
        Assert.Contains($"checkRecordId={FixtureCheckRecordId:D}", n, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateFromCheckFinalSubmit_IdenticalAndRequireDiff_ReturnsNull()
    {
        var dto = new OcrResultDto(
            "12345",
            10.5m,
            new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>(),
            RoutingNumber: "021000021",
            AccountNumber: "987654321");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        var ach = CheckAchExtensionData.FromOcrResult(dto);

        var sample = SubmitCheckOcrTrainingSampleFactory.TryCreateFromCheckFinalSubmit(
            doc,
            "https://minio/bucket/c.jpg",
            FixtureOcrResultId,
            "12345",
            10.5m,
            new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            ach,
            requireStructuredDiff: true,
            checkRecordId: FixtureCheckRecordId);

        Assert.Null(sample);
    }

    [Fact]
    public void TryCreateFromCheckFinalSubmit_CheckNumberDiff_ReturnsSample()
    {
        var dto = new OcrResultDto(
            "WRONG",
            10.5m,
            new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>(),
            RoutingNumber: "021000021");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        var correctDto = dto with { CheckNumber = "12345" };
        var submitted = CheckAchExtensionData.FromOcrResult(correctDto);

        var sample = SubmitCheckOcrTrainingSampleFactory.TryCreateFromCheckFinalSubmit(
            doc,
            "https://minio/bucket/c.jpg",
            FixtureOcrResultId,
            "12345",
            10.5m,
            new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            submitted,
            requireStructuredDiff: true,
            checkRecordId: FixtureCheckRecordId);

        Assert.NotNull(sample);
        Assert.Equal("check", sample.DocumentType);
        Assert.Equal("WRONG", sample.OcrCheckNumber);
        Assert.Equal("12345", sample.CorrectCheckNumber);
        Assert.Contains("// 支票 OCR 解析摘要", sample.OcrRawResponse, StringComparison.Ordinal);
        Assert.Contains(SubmitCheckOcrTrainingSampleFactory.AutoSubmitNotesPrefix, sample.Notes, StringComparison.Ordinal);
        Assert.Contains($"ocrResultId={FixtureOcrResultId:D}", sample.Notes, StringComparison.Ordinal);
        Assert.Contains($"checkRecordId={FixtureCheckRecordId:D}", sample.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateFromCheckFinalSubmit_IdenticalAndRequireDiffFalse_ReturnsSample()
    {
        var dto = new OcrResultDto(
            "1",
            1m,
            new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        var ach = CheckAchExtensionData.FromOcrResult(dto);

        var sample = SubmitCheckOcrTrainingSampleFactory.TryCreateFromCheckFinalSubmit(
            doc,
            "https://x/a.png",
            FixtureOcrResultId,
            "1",
            1m,
            new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            ach,
            requireStructuredDiff: false);

        Assert.NotNull(sample);
    }

    [Fact]
    public void TryCreateFromCheckFinalSubmit_MicrFieldOrderNoteOnlyOnOcr_NoSampleWhenOtherFieldsMatch()
    {
        var dto = new OcrResultDto(
            "1",
            1m,
            new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double>(),
            MicrFieldOrderNote: "ocr-only-note");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        var formAch = new CheckAchExtensionData(
            null, null, null, null, null, null, null, null, null, null, null, null);

        var sample = SubmitCheckOcrTrainingSampleFactory.TryCreateFromCheckFinalSubmit(
            doc,
            "https://x/a.png",
            FixtureOcrResultId,
            "1",
            1m,
            new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            formAch,
            requireStructuredDiff: true);

        Assert.Null(sample);
    }

    [Fact]
    public void CheckAchExtensionData_EqualsForTraining_MatchesInfrastructureSemantics()
    {
        var a = new CheckAchExtensionData("021000021", "1", null, null, null, null, null, null, null, null, null, null);
        var b = new CheckAchExtensionData("021000021", "1", null, null, null, null, null, null, null, null, null, null);
        Assert.True(CheckAchExtensionData.EqualsForTraining(a, b));

        var c = new CheckAchExtensionData("021000021", "2", null, null, null, null, null, null, null, null, null, null);
        Assert.False(CheckAchExtensionData.EqualsForTraining(a, c));
    }
}
