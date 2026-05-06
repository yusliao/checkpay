using CheckPay.Infrastructure.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CheckPay.Tests.Infrastructure;

public class CheckImagePreprocessorTests
{
    [Fact]
    public void TryPreprocess_Disabled_DoesNotReencode()
    {
        using var img = new Image<Rgba32>(16, 16, new Rgba32(255, 255, 255, 255));
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        ms.Position = 0;
        var options = new CheckImagePreprocessOptions { Enabled = false };
        var r = CheckImagePreprocessor.TryPreprocessForCheck(ms, options);
        Assert.False(r.UsedPreprocessed);
        Assert.Equal("none", r.Mode);
        r.Stream.Dispose();
    }

    [Fact]
    public void TryPreprocess_Enabled_ProducesJpegAndPassthroughWhenNoGeometryChange()
    {
        using var img = new Image<Rgba32>(64, 32, new Rgba32(255, 255, 255, 255));
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        ms.Position = 0;

        var options = new CheckImagePreprocessOptions
        {
            Enabled = true,
            DeskewEnabled = false,
            ContentTrimEnabled = false,
            MinMaxSidePixels = 0
        };
        var r = CheckImagePreprocessor.TryPreprocessForCheck(ms, options);
        Assert.True(r.UsedPreprocessed);
        Assert.Equal(0, r.SkewDegreesApplied, precision: 5);
        Assert.Equal("passthrough", r.Mode);
        Assert.True(r.Stream.Length > 0);
        r.Stream.Dispose();
    }

    [Fact]
    public void EstimateSkewDegrees_WithHorizontalStripes_PrefersNearZeroOnAlreadyStraightImage()
    {
        using var img = new Image<Rgba32>(200, 80, new Rgba32(255, 255, 255, 255));
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                if (y % 10 >= 3)
                    continue;
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(0, 0, 0, 255);
            }
        });
        using var probe = img.Clone(x => x.Resize(140, 56)).CloneAs<L8>();
        var angle = CheckImagePreprocessor.EstimateSkewDegrees(probe, 12, 1.5);
        Assert.True(Math.Abs(angle) < 4, $"skew estimate {angle} should be small for horizontal pattern");
    }
}
