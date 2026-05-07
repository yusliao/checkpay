using System.Text.RegularExpressions;
using CheckPay.Application.Common.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Rectangle = SixLabors.ImageSharp.Rectangle;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 依据首轮 Read 行框估算「票面数字金额 + 手写大写」带状 ROI，裁剪后二次 Vision Read，降低全页噪声导致的数字粘连误判。
/// </summary>
internal static class CheckOcrAmountRoiRefinement
{
    private static readonly Regex LegalAmountHintRegex = new(
        @"(?i)\b(thousand|hundred|million|dollars?|only|cents?)\b",
        RegexOptions.Compiled);

    /// <summary>裁剪图内视为整幅均为金额候选区；MICR 带放到不可达坐标避免降权。</summary>
    public static CheckOcrParsingProfile CroppedAmountParseProfile { get; } = new()
    {
        AmountPriorRegion = new NormRegion(0, 0, 1, 1),
        MicrPriorRegion = new NormRegion(1.01, 1.01, 1.02, 1.02)
    };

    public static NormRegion? TryGetAmountRefinementNormRegion(
        ReadOcrLayout layout,
        CheckOcrParsingProfile profile,
        double maxRoiNormArea)
    {
        if (layout.Lines.Count == 0 || layout.ImageWidth <= 0 || layout.ImageHeight <= 0)
            return null;

        var micr = profile.MicrPriorRegion;
        var amountR = profile.AmountPriorRegion;

        var expandLines = new List<ReadOcrLine>();
        foreach (var line in layout.Lines)
        {
            if (micr?.Contains(line.NormCenterX, line.NormCenterY) == true)
                continue;
            if (line.NormCenterY >= 0.74)
                continue;

            var t = line.Text.Trim();
            if (t.Length == 0)
                continue;

            if (t.Contains('$', StringComparison.Ordinal))
            {
                expandLines.Add(line);
                continue;
            }

            if (amountR?.Contains(line.NormCenterX, line.NormCenterY) == true
                && Regex.IsMatch(t, @"\d"))
            {
                expandLines.Add(line);
                continue;
            }

            if (LegalAmountHintRegex.IsMatch(t)
                && !t.Contains('$', StringComparison.Ordinal)
                && line.NormCenterX < 0.88)
                expandLines.Add(line);
        }

        if (expandLines.Count == 0)
            return null;

        var minX = expandLines.Min(l => l.NormLeft);
        var maxX = expandLines.Max(l => l.NormRight);
        var minY = expandLines.Min(l => l.NormTop);
        var maxY = expandLines.Max(l => l.NormBottom);

        const double padX = 0.035;
        const double padY = 0.035;
        minX = Math.Clamp(minX - padX, 0, 1);
        maxX = Math.Clamp(maxX + padX, 0, 1);
        minY = Math.Clamp(minY - padY, 0, 1);
        maxY = Math.Clamp(Math.Min(maxY + padY, 0.72), 0, 1);

        if (maxX - minX < 0.12 || maxY - minY < 0.05)
            return null;
        if ((maxX - minX) * (maxY - minY) > maxRoiNormArea)
            return null;

        return new NormRegion(minX, minY, maxX, maxY);
    }

    /// <summary>将图像按归一化 ROI 裁剪为 PNG 字节流（调用方负责释放）。</summary>
    public static MemoryStream? TryCropImageToNormRegion(Stream imageStream, NormRegion region)
    {
        try
        {
            imageStream.Position = 0;
            using var image = Image.Load(imageStream);
            var x0 = (int)Math.Floor(region.MinNormX * image.Width);
            var y0 = (int)Math.Floor(region.MinNormY * image.Height);
            var x1 = (int)Math.Ceiling(region.MaxNormX * image.Width);
            var y1 = (int)Math.Ceiling(region.MaxNormY * image.Height);
            x0 = Math.Clamp(x0, 0, image.Width - 1);
            y0 = Math.Clamp(y0, 0, image.Height - 1);
            x1 = Math.Clamp(x1, x0 + 1, image.Width);
            y1 = Math.Clamp(y1, y0 + 1, image.Height);
            var w = x1 - x0;
            var h = y1 - y0;
            if (w < 24 || h < 24)
                return null;

            image.Mutate(ctx => ctx.Crop(new Rectangle(x0, y0, w, h)));
            var outMs = new MemoryStream();
            image.SaveAsPng(outMs);
            outMs.Position = 0;
            return outMs;
        }
        catch
        {
            return null;
        }
    }
}
