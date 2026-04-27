using System.Text.Json.Serialization;

namespace CheckPay.Application.Common.Models;

/// <summary>归一化坐标区域（0~1，相对图像宽高）。</summary>
public sealed record NormRegion(
    [property: JsonPropertyName("minNormX")] double MinNormX,
    [property: JsonPropertyName("minNormY")] double MinNormY,
    [property: JsonPropertyName("maxNormX")] double MaxNormX,
    [property: JsonPropertyName("maxNormY")] double MaxNormY)
{
    public bool Contains(double normCenterX, double normCenterY) =>
        normCenterX >= MinNormX && normCenterX <= MaxNormX
        && normCenterY >= MinNormY && normCenterY <= MaxNormY;
}

/// <summary>支票 Vision Read 后处理版式提示（可由 ocr_check_templates.parsing_profile_json 配置）。</summary>
public sealed class CheckOcrParsingProfile
{
    [JsonPropertyName("amountPriorRegion")]
    public NormRegion? AmountPriorRegion { get; set; }

    [JsonPropertyName("micrPriorRegion")]
    public NormRegion? MicrPriorRegion { get; set; }

    [JsonPropertyName("datePriorRegion")]
    public NormRegion? DatePriorRegion { get; set; }

    /// <summary>右上象限（印刷支票号常见区域），若 JSON 未提供则使用默认值。</summary>
    [JsonPropertyName("printedCheckPriorRegion")]
    public NormRegion? PrintedCheckPriorRegion { get; set; }

    public static CheckOcrParsingProfile CreateDefault() => new()
    {
        AmountPriorRegion = new NormRegion(0.48, 0.0, 1.0, 0.52),
        MicrPriorRegion = new NormRegion(0.0, 0.72, 1.0, 1.0),
        DatePriorRegion = new NormRegion(0.0, 0.0, 0.55, 0.42),
        PrintedCheckPriorRegion = new NormRegion(0.52, 0.0, 1.0, 0.42)
    };

    public static readonly CheckOcrParsingProfile Default = CreateDefault();

    /// <summary>将部分 JSON 与内置默认合并，避免缺字段导致解析退化。</summary>
    public static CheckOcrParsingProfile MergeDefaults(CheckOcrParsingProfile? partial)
    {
        var d = Default;
        if (partial == null)
            return d;

        return new CheckOcrParsingProfile
        {
            AmountPriorRegion = partial.AmountPriorRegion ?? d.AmountPriorRegion,
            MicrPriorRegion = partial.MicrPriorRegion ?? d.MicrPriorRegion,
            DatePriorRegion = partial.DatePriorRegion ?? d.DatePriorRegion,
            PrintedCheckPriorRegion = partial.PrintedCheckPriorRegion ?? d.PrintedCheckPriorRegion
        };
    }
}
