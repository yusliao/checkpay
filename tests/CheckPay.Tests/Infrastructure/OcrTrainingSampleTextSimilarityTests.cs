using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public class OcrTrainingSampleTextSimilarityTests
{
    [Fact]
    public void DiceCoefficient_IdenticalText_ReturnsOne()
    {
        var s = "routing number chase bank pay order";
        var d = OcrTrainingSampleTextSimilarity.DiceCoefficient(s, s);
        Assert.Equal(1.0, d, 5);
    }

    [Fact]
    public void DiceCoefficient_Disjoint_ReturnsZero()
    {
        var d = OcrTrainingSampleTextSimilarity.DiceCoefficient("alpha beta gamma", "zzz qqq vvv");
        Assert.Equal(0.0, d);
    }

    [Fact]
    public void FallbackFingerprint_IsStable()
    {
        var dto = new OcrResultDto(
            "1234",
            99.50m,
            new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, double> { ["Amount"] = 0.5 });
        var fp = OcrTrainingSampleTextSimilarity.FallbackFingerprint(dto);
        Assert.Equal("1234|99.50|20240501", fp);
    }
}
