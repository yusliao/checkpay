using Microsoft.Extensions.Configuration;

namespace CheckPay.Infrastructure.Services;

/// <summary>支票送 Vision Read 前的可选几何预处理（客户上传图无法控制拍摄条件）。</summary>
public sealed class CheckImagePreprocessOptions
{
    /// <summary>总开关；未配置节时默认关闭，避免升级后行为突变。</summary>
    public bool Enabled { get; init; } = false;

    public bool DeskewEnabled { get; init; } = true;

    /// <summary>搜索最佳纠倾角时的绝对上限（度）。</summary>
    public double DeskewMaxDegrees { get; init; } = 15;

    /// <summary>检测到的角度绝对值低于此值则不旋转（避免噪声误旋）。</summary>
    public double DeskewMinApplyDegrees { get; init; } = 0.35;

    public double DeskewStepDegrees { get; init; } = 1.0;

    /// <summary>轴对齐裁剪：在缩小图上用亮度阈值找非「大块亮衬底」区域的外接框，去掉深色桌布边（真透视需专用流程）。</summary>
    public bool ContentTrimEnabled { get; init; } = true;

    /// <summary>暗色像素覆盖缩小图的比例需达到此下限才认为裁剪可信。</summary>
    public double ContentMinCoverageRatio { get; init; } = 0.12;

    /// <summary>裁切内边距占框宽/高的比例。</summary>
    public double ContentPaddingRatio { get; init; } = 0.02;

    /// <summary>用于估角的缩小边；越大越准、越慢。</summary>
    public int DeskewProbeMaxSide { get; init; } = 560;

    /// <summary>处理后若长边短于此则等比放大（提高 MICR 等效分辨率）。</summary>
    public int MinMaxSidePixels { get; init; } = 1280;

    public int MinMaxSideUpperClamp { get; init; } = 4096;

    public bool FallbackToOriginalOnError { get; init; } = true;

    public static CheckImagePreprocessOptions FromConfiguration(IConfigurationSection? section)
    {
        if (section is null || !section.Exists())
            return new CheckImagePreprocessOptions();

        return new CheckImagePreprocessOptions
        {
            Enabled = ParseBool(section["Enabled"], defaultValue: false),
            DeskewEnabled = ParseBool(section["DeskewEnabled"], defaultValue: true),
            DeskewMaxDegrees = ParseDouble(section["DeskewMaxDegrees"], 15, 3, 25),
            DeskewMinApplyDegrees = ParseDouble(section["DeskewMinApplyDegrees"], 0.35, 0, 3),
            DeskewStepDegrees = ParseDouble(section["DeskewStepDegrees"], 1, 0.25, 3),
            ContentTrimEnabled = ParseBool(section["ContentTrimEnabled"], defaultValue: true),
            ContentMinCoverageRatio = ParseDouble(section["ContentMinCoverageRatio"], 0.12, 0.04, 0.5),
            ContentPaddingRatio = ParseDouble(section["ContentPaddingRatio"], 0.02, 0, 0.12),
            DeskewProbeMaxSide = ParseInt(section["DeskewProbeMaxSide"], 560, 320, 1200),
            MinMaxSidePixels = ParseInt(section["MinMaxSidePixels"], 1280, 800, 4096),
            MinMaxSideUpperClamp = ParseInt(section["MinMaxSideUpperClamp"], 4096, 2048, 8192),
            FallbackToOriginalOnError = ParseBool(section["FallbackToOriginalOnError"], defaultValue: true)
        };
    }

    private static bool ParseBool(string? raw, bool defaultValue) =>
        bool.TryParse(raw, out var v) ? v : defaultValue;

    private static double ParseDouble(string? raw, double def, double min, double max)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return Math.Clamp(v, min, max);
        return def;
    }

    private static int ParseInt(string? raw, int def, int min, int max)
    {
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return Math.Clamp(v, min, max);
        return def;
    }
}
