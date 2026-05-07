using CheckPay.Application.Common.Models;
using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public sealed class CheckOcrAmountArbitrationTests
{
    [Fact]
    public void TryRepairEmbeddedCentsInScaledDollar_604805_05_Returns6048_05()
    {
        Assert.True(CheckOcrAmountArbitration.TryRepairEmbeddedCentsInScaledDollar(604805.05m, out var r));
        Assert.Equal(6048.05m, r);
    }

    [Fact]
    public void TryRepairEmbeddedCentsInScaledDollar_NoFraction_ReturnsFalse()
    {
        Assert.False(CheckOcrAmountArbitration.TryRepairEmbeddedCentsInScaledDollar(604805m, out _));
    }

    [Fact]
    public void LayoutSuggestsDigitAdhesion_DetectsLongDigitRunAfterDollar()
    {
        var profile = CheckOcrParsingProfile.Default;
        var lines = new[]
        {
            new ReadOcrLine("1 $ 604805", 0.5, 0.38, 0.36, 0.40, 0.28, 0.62),
            new ReadOcrLine("⑆063114030⑆", 0.9, 0.92, 0.88, 0.94, 0.1, 0.9)
        };
        var layout = new ReadOcrLayout(string.Join('\n', lines.Select(l => l.Text)), lines, 1200, 800);
        Assert.True(CheckOcrAmountArbitration.LayoutSuggestsDigitAdhesion(layout, profile));
    }

    [Fact]
    public void TryGetAmountRefinementNormRegion_UnionDollarAndLegalLines()
    {
        var profile = CheckOcrParsingProfile.Default;
        var lines = new[]
        {
            new ReadOcrLine("Six thousand forty-eight Dollars", 0.25, 0.42, 0.40, 0.44, 0.05, 0.55),
            new ReadOcrLine("$ 6048 05", 0.72, 0.42, 0.40, 0.44, 0.58, 0.92)
        };
        var layout = new ReadOcrLayout(string.Join('\n', lines.Select(l => l.Text)), lines, 1000, 1000);
        var r = CheckOcrAmountRoiRefinement.TryGetAmountRefinementNormRegion(layout, profile, 0.55);
        Assert.NotNull(r);
        Assert.True(r!.MaxNormX - r.MinNormX >= 0.12);
        Assert.True(r.MaxNormY <= 0.72);
    }

    [Fact]
    public void TryApplyEmbeddedCentsRepairWithConfirmation_AppliesWhenWordMatches()
    {
        var di = new PrebuiltCheckStructuredFields
        {
            WordAmountParsed = 6048.05m,
            WordAmountConfidence = 0.66
        };
        var diag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var amt = 604805.05m;
        var conf = 0.72;
        Assert.True(CheckOcrAmountArbitration.TryApplyEmbeddedCentsRepairWithConfirmation(
            ref amt,
            ref conf,
            di,
            string.Empty,
            diag));
        Assert.Equal(6048.05m, amt);
        Assert.Equal("applied", diag["amount_embedded_cents_repair"]);
    }

    [Fact]
    public void TryApplyMultiSourceConsensus_TwoSourcesAgree_ReducesGiantMerged()
    {
        var di = new PrebuiltCheckStructuredFields
        {
            NumberAmount = 6048.05m,
            NumberAmountConfidence = 0.65,
            WordAmountParsed = 6048.05m,
            WordAmountConfidence = 0.58
        };
        var diag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var amt = 604805.05m;
        var conf = 0.82;
        Assert.True(CheckOcrAmountArbitration.TryApplyMultiSourceConsensus(
            ref amt,
            ref conf,
            di,
            string.Empty,
            diag));
        Assert.Equal(6048.05m, amt);
        Assert.Contains("consensus_", diag["amount_multisource_arbitration"]);
    }

    [Fact]
    public void ParseAmountRoiSecondPassMode_Always_IsCaseInsensitive()
    {
        Assert.Equal(AmountRoiSecondPassMode.Always, CheckOcrAmountArbitration.ParseAmountRoiSecondPassMode("always"));
        Assert.Equal(AmountRoiSecondPassMode.OnDemand, CheckOcrAmountArbitration.ParseAmountRoiSecondPassMode(null));
        Assert.Equal(AmountRoiSecondPassMode.OnDemand, CheckOcrAmountArbitration.ParseAmountRoiSecondPassMode("OnDemand"));
    }

    [Fact]
    public void ShouldTriggerRoiSecondPass_Always_IgnoresHighConfidence()
    {
        var profile = CheckOcrParsingProfile.Default;
        var lines = new[] { new ReadOcrLine("$100.00", 0.9, 0.4, 0.38, 0.42, 0.7, 0.95) };
        var layout = new ReadOcrLayout(lines[0].Text, lines, 800, 600);
        Assert.True(CheckOcrAmountArbitration.ShouldTriggerRoiSecondPass(
            AmountRoiSecondPassMode.Always,
            visionConfTrigger: 0.99,
            amount: 100m,
            amountConf: 0.95,
            layout,
            profile,
            rawText: lines[0].Text));
    }

    [Fact]
    public void TryApplyMultiSourceConsensus_RoiCropDi_CanFormClusterWithMerged()
    {
        var di = new PrebuiltCheckStructuredFields { NumberAmount = 9999.99m, NumberAmountConfidence = 0.7 };
        var roiCrop = new PrebuiltCheckStructuredFields
        {
            NumberAmount = 6048.05m,
            NumberAmountConfidence = 0.72,
            WordAmountParsed = 6048.05m,
            WordAmountConfidence = 0.66
        };
        var diag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var amt = 604805.05m;
        var conf = 0.75;
        Assert.True(CheckOcrAmountArbitration.TryApplyMultiSourceConsensus(
            ref amt,
            ref conf,
            di,
            string.Empty,
            diag,
            roiCrop));
        Assert.Equal(6048.05m, amt);
    }
}
