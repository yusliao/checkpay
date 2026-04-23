using System.Text;
using System.Text.RegularExpressions;

namespace CheckPay.Application.Common;

/// <summary>支票 Pay to the order of 固定收款人选项及 OCR 文本归一匹配。</summary>
public static partial class PayToOrderOfCatalog
{
    public const string CheungKongAllianceFood =
        "CHEUNG KONG HOLDING HOLDING INC. DBA ALLIANCE FOOD GROUP";

    public const string MaxwellExcelFood =
        "MAXWELL TRADING INC. DBA EXCEL FOOD SERVICES";

    public static readonly string[] All = [CheungKongAllianceFood, MaxwellExcelFood];

    /// <summary>
    /// 将 OCR 或手工粘贴的 Pay to 文本匹配到上述两家之一；无法区分时返回 null。
    /// </summary>
    public static string? MatchCanonical(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return null;

        var trimmed = ocrText.Trim();
        foreach (var c in All)
        {
            if (trimmed.Equals(c, StringComparison.OrdinalIgnoreCase))
                return c;
        }

        var n = NormalizeForMatch(trimmed);

        var scoreAlliance = ScoreAlliance(n);
        var scoreMaxwell = ScoreMaxwell(n);
        if (scoreAlliance == 0 && scoreMaxwell == 0)
            return null;
        if (scoreAlliance > scoreMaxwell)
            return CheungKongAllianceFood;
        if (scoreMaxwell > scoreAlliance)
            return MaxwellExcelFood;

        // 平分：看谁的整体规范名在 OCR 归一化串中更像「被包含」
        var na = NormalizeForMatch(CheungKongAllianceFood);
        var nm = NormalizeForMatch(MaxwellExcelFood);
        var ca = ContainmentScore(n, na);
        var cm = ContainmentScore(n, nm);
        if (ca > cm) return CheungKongAllianceFood;
        if (cm > ca) return MaxwellExcelFood;

        return null;
    }

    private static int ScoreAlliance(string n)
    {
        var s = 0;
        if (n.Contains("CHEUNG", StringComparison.Ordinal)) s += 3;
        if (n.Contains("KONG", StringComparison.Ordinal)) s += 2;
        if (n.Contains("ALLIANCE", StringComparison.Ordinal)) s += 4;
        return s;
    }

    private static int ScoreMaxwell(string n)
    {
        var s = 0;
        if (n.Contains("MAXWELL", StringComparison.Ordinal)) s += 4;
        if (n.Contains("EXCEL", StringComparison.Ordinal)) s += 3;
        if (n.Contains("TRADING", StringComparison.Ordinal)) s += 2;
        return s;
    }

    private static int ContainmentScore(string ocrNorm, string canonicalNorm)
    {
        if (ocrNorm.Length == 0 || canonicalNorm.Length == 0) return 0;
        if (ocrNorm.Contains(canonicalNorm, StringComparison.Ordinal)) return canonicalNorm.Length;
        if (canonicalNorm.Contains(ocrNorm, StringComparison.Ordinal)) return ocrNorm.Length;

        // 公共 token 数量（长度≥3）
        var ocrTokens = ocrNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var canTokens = canonicalNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>(canTokens, StringComparer.Ordinal);
        var hit = 0;
        foreach (var t in ocrTokens)
        {
            if (t.Length >= 3 && set.Contains(t))
                hit++;
        }

        return hit;
    }

    private static string NormalizeForMatch(string s)
    {
        var upper = s.ToUpperInvariant();
        var sb = new StringBuilder(upper.Length);
        foreach (var ch in upper)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else
                sb.Append(' ');
        }

        return Spaces().Replace(sb.ToString().Trim(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
