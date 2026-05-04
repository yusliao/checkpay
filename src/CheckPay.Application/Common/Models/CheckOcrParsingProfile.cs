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

    [JsonPropertyName("bankNamePriorRegion")]
    public NormRegion? BankNamePriorRegion { get; set; }

    [JsonPropertyName("accountHolderPriorRegion")]
    public NormRegion? AccountHolderPriorRegion { get; set; }

    /// <summary>票面印刷公司名/商号常见区域（支票号下方至 Pay to 上方，多含 INC./LLC 等后缀）。未配置时默认含页面上部窄带（含 normY 较低的一行商号）。</summary>
    [JsonPropertyName("companyNamePriorRegion")]
    public NormRegion? CompanyNamePriorRegion { get; set; }

    /// <summary>票面印刷地址（常在商号/INC 行下方：门牌+街道 与 城市 ST 邮编 两行）。</summary>
    [JsonPropertyName("accountAddressPriorRegion")]
    public NormRegion? AccountAddressPriorRegion { get; set; }

    public static CheckOcrParsingProfile CreateDefault() => new()
    {
        AmountPriorRegion = new NormRegion(0.48, 0.0, 1.0, 0.52),
        MicrPriorRegion = new NormRegion(0.0, 0.72, 1.0, 1.0),
        DatePriorRegion = new NormRegion(0.0, 0.0, 0.55, 0.42),
        PrintedCheckPriorRegion = new NormRegion(0.52, 0.0, 1.0, 0.42),
        BankNamePriorRegion = new NormRegion(0.0, 0.0, 0.62, 0.28),
        AccountHolderPriorRegion = new NormRegion(0.0, 0.22, 0.76, 0.62),
        CompanyNamePriorRegion = new NormRegion(0.0, 0.0, 0.99, 0.62),
        // minNormY 需覆盖「商号行 normY 仍较靠上」时紧接其下的门牌街道行（常见 <0.22），否则街道行会整行掉出地址带
        AccountAddressPriorRegion = new NormRegion(0.0, 0.06, 0.90, 0.74)
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
            PrintedCheckPriorRegion = partial.PrintedCheckPriorRegion ?? d.PrintedCheckPriorRegion,
            BankNamePriorRegion = partial.BankNamePriorRegion ?? d.BankNamePriorRegion,
            AccountHolderPriorRegion = partial.AccountHolderPriorRegion ?? d.AccountHolderPriorRegion,
            CompanyNamePriorRegion = partial.CompanyNamePriorRegion ?? d.CompanyNamePriorRegion,
            AccountAddressPriorRegion = partial.AccountAddressPriorRegion ?? d.AccountAddressPriorRegion
        };
    }
}
