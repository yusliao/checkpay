using System.Globalization;
using System.Text.RegularExpressions;
using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

internal static class OcrTrainingSampleTextSimilarity
{
    private static readonly Regex TokenSplitter = new(@"[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Dice 系数：2|A∩B|/(|A|+|B|)，基于长度≥2 的 token。</summary>
    public static double DiceCoefficient(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return 0.0;

        var sa = Tokenize(a);
        var sb = Tokenize(b);
        if (sa.Count == 0 || sb.Count == 0)
            return 0.0;

        var inter = 0;
        foreach (var t in sa)
        {
            if (sb.Contains(t))
                inter++;
        }

        return 2.0 * inter / (sa.Count + sb.Count);
    }

    private static HashSet<string> Tokenize(string s)
    {
        var parts = TokenSplitter.Split(s.ToLowerInvariant());
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parts)
        {
            if (p.Length >= 2)
                set.Add(p);
        }

        return set;
    }

    public static string FallbackFingerprint(OcrResultDto dto)
    {
        var cn = dto.CheckNumber?.Trim() ?? string.Empty;
        var amt = dto.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var dt = dto.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return $"{cn}|{amt}|{dt}";
    }
}
