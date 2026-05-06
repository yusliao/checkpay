using System.Text.Json;
using CheckPay.Application.Common;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

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
}
