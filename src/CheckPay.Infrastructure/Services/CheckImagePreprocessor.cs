using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 客户上传支票：纠倾、轴对齐内容裁剪（去深色衬底边）、最小边放大；改善 Read 行聚类与 MICR 等效分辨率。
/// </summary>
public static class CheckImagePreprocessor
{
    /// <summary>单张支票 Read +（可选）DI 融合共用同一预处理结果。</summary>
    public static CheckImagePreprocessResult TryPreprocessForCheck(Stream input, CheckImagePreprocessOptions options, ILogger? logger = null)
    {
        if (!options.Enabled)
            return CopyAsResult(input, "disabled");

        try
        {
            var loaded = Image.Load<Rgba32>(input);
            return ProcessLoaded(loaded, options, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "支票图像预处理失败");
            if (options.FallbackToOriginalOnError)
            {
                input.Position = 0;
                return CopyAsResult(input, "error_fallback");
            }

            throw;
        }
    }

    private static CheckImagePreprocessResult ProcessLoaded(Image<Rgba32> image, CheckImagePreprocessOptions options, ILogger? logger)
    {
        var modeParts = new List<string>();
        double skewDetected = 0;
        double skewApplied = 0;
        var contentTrimApplied = false;
        var upscaleApplied = false;
        var work = image;

        try
        {
            if (options.ContentTrimEnabled
                && TryAxisAlignedContentCrop(work, options, out var cropped)
                && cropped is not null)
            {
                work.Dispose();
                work = cropped;
                contentTrimApplied = true;
                modeParts.Add("content_trim");
            }

            if (options.DeskewEnabled)
            {
                using var probe = BuildDeskewProbe(work, options.DeskewProbeMaxSide);
                skewDetected = EstimateSkewDegrees(probe, options.DeskewMaxDegrees, options.DeskewStepDegrees);
                if (Math.Abs(skewDetected) >= options.DeskewMinApplyDegrees
                    && Math.Abs(skewDetected) <= options.DeskewMaxDegrees + 0.001)
                {
                    work.Mutate(x => x.Rotate((float)skewDetected));
                    skewApplied = skewDetected;
                    modeParts.Add("deskew");
                }
            }

            if (options.MinMaxSidePixels > 0)
            {
                var maxSide = Math.Max(work.Width, work.Height);
                if (maxSide < options.MinMaxSidePixels && maxSide > 0)
                {
                    var scale = Math.Min(
                        (double)options.MinMaxSidePixels / maxSide,
                        (double)options.MinMaxSideUpperClamp / maxSide);
                    if (scale > 1.01)
                    {
                        var w = Math.Clamp((int)Math.Round(work.Width * scale), 1, options.MinMaxSideUpperClamp);
                        var h = Math.Clamp((int)Math.Round(work.Height * scale), 1, options.MinMaxSideUpperClamp);
                        work.Mutate(x => x.Resize(w, h));
                        upscaleApplied = true;
                        modeParts.Add("upscale");
                    }
                }
            }

            var outStream = new MemoryStream();
            work.SaveAsJpeg(outStream, new JpegEncoder { Quality = 92 });
            outStream.Position = 0;

            var mode = modeParts.Count > 0 ? string.Join('+', modeParts) : "passthrough";
            logger?.LogInformation(
                "支票图像预处理: mode={Mode}, skewDetected={SkewD:F2}, skewApplied={SkewA:F2}, contentTrim={Trim}, upscale={Up}",
                mode,
                skewDetected,
                skewApplied,
                contentTrimApplied,
                upscaleApplied);

            return new CheckImagePreprocessResult(
                outStream,
                UsedPreprocessed: true,
                SkewDegreesDetected: skewDetected,
                SkewDegreesApplied: skewApplied,
                ContentTrimApplied: contentTrimApplied,
                MinSideUpscaleApplied: upscaleApplied,
                Mode: mode,
                FallbackReason: null);
        }
        finally
        {
            work.Dispose();
        }
    }

    /// <summary>估计“纸张”区域轴对齐外接框，裁掉大块深色衬底边（透视强时仅作近似，真透视拉平需客户端或专用模型）。</summary>
    private static bool TryAxisAlignedContentCrop(Image<Rgba32> image, CheckImagePreprocessOptions options, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Image<Rgba32>? cropped)
    {
        cropped = null;
        var w = image.Width;
        var h = image.Height;
        if (w < 100 || h < 100)
            return false;

        var scale = Math.Min(360d / w, 360d / h);
        var sw = Math.Max(64, (int)Math.Round(w * scale));
        var sh = Math.Max(64, (int)Math.Round(h * scale));
        using var small = image.Clone(x => x.Resize(sw, sh));
        using var g = small.CloneAs<L8>();

        var sum = 0L;
        var cnt = 0;
        g.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    sum += row[x].PackedValue;
                    cnt++;
                }
            }
        });

        if (cnt == 0)
            return false;

        var mean = sum / (double)cnt;
        // 深色衬底上支票纸偏亮：取亮于均值的连续区域外接框（文字为暗点，框仍大致包住纸面）
        var tPaper = Math.Clamp(mean + 8, 35, 245);

        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        var any = false;
        g.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].PackedValue < tPaper)
                        continue;
                    any = true;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        });

        if (!any || maxX <= minX || maxY <= minY)
            return false;

        var boxW = maxX - minX + 1;
        var boxH = maxY - minY + 1;
        var frac = boxW * boxH / (double)(sw * sh);
        if (frac < options.ContentMinCoverageRatio || frac > 0.97)
            return false;

        var padX = (int)Math.Round(boxW * options.ContentPaddingRatio);
        var padY = (int)Math.Round(boxH * options.ContentPaddingRatio);
        minX = Math.Max(0, minX - padX);
        maxX = Math.Min(sw - 1, maxX + padX);
        minY = Math.Max(0, minY - padY);
        maxY = Math.Min(sh - 1, maxY + padY);

        var inv = 1 / scale;
        var x0 = Math.Clamp((int)Math.Floor(minX * inv), 0, w - 1);
        var y0 = Math.Clamp((int)Math.Floor(minY * inv), 0, h - 1);
        var x1 = Math.Clamp((int)Math.Ceiling((maxX + 1) * inv), x0 + 1, w);
        var y1 = Math.Clamp((int)Math.Ceiling((maxY + 1) * inv), y0 + 1, h);
        var cw = x1 - x0;
        var ch = y1 - y0;
        if (cw < w * 0.35 || ch < h * 0.35)
            return false;

        cropped = image.Clone(x => x.Crop(new Rectangle(x0, y0, cw, ch)));
        return true;
    }

    private static Image<L8> BuildDeskewProbe(Image<Rgba32> image, int maxSide)
    {
        var scale = Math.Min(1d, maxSide / (double)Math.Max(image.Width, image.Height));
        var w = Math.Max(32, (int)Math.Round(image.Width * scale));
        var h = Math.Max(32, (int)Math.Round(image.Height * scale));
        return image.Clone(x => x.Resize(w, h)).CloneAs<L8>();
    }

    /// <summary>在投影轮廓方差最大处取纠倾角（度；用于 <see cref="ImageProcessingContext.Rotate(float)"/>）。</summary>
    public static double EstimateSkewDegrees(Image<L8> grayProbe, double maxDeg, double step)
    {
        if (grayProbe.Width < 16 || grayProbe.Height < 16)
            return 0;

        var bestAngle = 0d;
        var bestScore = double.MinValue;
        for (var a = -maxDeg; a <= maxDeg + 0.0001; a += step)
        {
            using var rotated = grayProbe.CloneAs<L8>();
            rotated.Mutate(x => x.Rotate((float)a));
            var score = HorizontalProjectionVariance(rotated);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = a;
            }
        }

        return bestAngle;
    }

    private static double HorizontalProjectionVariance(Image<L8> img)
    {
        var sums = new double[img.Height];
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                double s = 0;
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    s += 255 - row[x].PackedValue;
                sums[y] = s;
            }
        });

        if (sums.Length == 0)
            return 0;
        double mean = 0;
        foreach (var t in sums)
            mean += t;
        mean /= sums.Length;
        double v = 0;
        foreach (var t in sums)
        {
            var d = t - mean;
            v += d * d;
        }

        return v / sums.Length;
    }

    private static CheckImagePreprocessResult CopyAsResult(Stream input, string reason)
    {
        var ms = new MemoryStream();
        input.Position = 0;
        input.CopyTo(ms);
        ms.Position = 0;
        return new CheckImagePreprocessResult(
            ms,
            UsedPreprocessed: false,
            SkewDegreesDetected: 0,
            SkewDegreesApplied: 0,
            ContentTrimApplied: false,
            MinSideUpscaleApplied: false,
            Mode: "none",
            FallbackReason: reason);
    }
}

public sealed record CheckImagePreprocessResult(
    MemoryStream Stream,
    bool UsedPreprocessed,
    double SkewDegreesDetected,
    double SkewDegreesApplied,
    bool ContentTrimApplied,
    bool MinSideUpscaleApplied,
    string Mode,
    string? FallbackReason);
