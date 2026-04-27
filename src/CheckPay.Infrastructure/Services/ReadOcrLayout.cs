using Azure.AI.Vision.ImageAnalysis;
using CheckPay.Application.Common.Models;

namespace CheckPay.Infrastructure.Services;

/// <summary>Vision Read 单行及其归一化几何中心（相对图像宽高 0~1）。</summary>
public sealed record ReadOcrLine(
    string Text,
    double NormCenterX,
    double NormCenterY,
    double NormTop,
    double NormBottom,
    double NormLeft,
    double NormRight);

/// <summary>整张图的 OCR 行布局与拼接全文。</summary>
public sealed record ReadOcrLayout(string FullText, IReadOnlyList<ReadOcrLine> Lines, int ImageWidth, int ImageHeight)
{
    public string ConcatLinesInRegion(NormRegion? r, string sep = "\n")
    {
        if (r is null || Lines.Count == 0)
            return FullText;

        var picked = Lines.Where(l => r.Contains(l.NormCenterX, l.NormCenterY)).Select(l => l.Text).ToList();
        return picked.Count == 0 ? string.Empty : string.Join(sep, picked);
    }
}

internal static class AzureReadLayoutExtractor
{
    public static ReadOcrLayout From(ImageAnalysisResult result)
    {
        if (result.Read?.Blocks is null || result.Read.Blocks.Count == 0)
            return new ReadOcrLayout(string.Empty, Array.Empty<ReadOcrLine>(), 0, 0);

        var w = result.Metadata?.Width ?? 0;
        var h = result.Metadata?.Height ?? 0;

        var lines = new List<ReadOcrLine>();
        foreach (var block in result.Read.Blocks)
        {
            foreach (var line in block.Lines)
            {
                var text = line.Text?.Trim() ?? string.Empty;
                if (text.Length == 0)
                    continue;

                var (cx, cy, top, bottom, left, right) = NormalizeBounds(line.BoundingPolygon, w, h);
                lines.Add(new ReadOcrLine(text, cx, cy, top, bottom, left, right));
            }
        }

        var fullText = string.Join("\n", lines.Select(l => l.Text));
        return new ReadOcrLayout(fullText, lines, w, h);
    }

    private static (double cx, double cy, double top, double bottom, double left, double right) NormalizeBounds(
        IReadOnlyList<ImagePoint>? polygon,
        int imageWidth,
        int imageHeight)
    {
        if (polygon is null || polygon.Count == 0)
            return (0.5, 0.5, 0.5, 0.5, 0.5, 0.5);

        var xs = polygon.Select(p => (double)p.X).ToList();
        var ys = polygon.Select(p => (double)p.Y).ToList();
        var minX = xs.Min();
        var maxX = xs.Max();
        var minY = ys.Min();
        var maxY = ys.Max();

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            var cx0 = (minX + maxX) / 2.0;
            var cy0 = (minY + maxY) / 2.0;
            return (cx0, cy0, minY, maxY, minX, maxX);
        }

        var cx = (minX + maxX) / 2.0 / imageWidth;
        var cy = (minY + maxY) / 2.0 / imageHeight;
        var top = minY / imageHeight;
        var bottom = maxY / imageHeight;
        var left = minX / imageWidth;
        var right = maxX / imageWidth;
        return (cx, cy, top, bottom, left, right);
    }
}
