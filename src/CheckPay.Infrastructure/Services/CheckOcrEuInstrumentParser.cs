using System.Text;
using System.Text.RegularExpressions;

namespace CheckPay.Infrastructure.Services;

/// <summary>欧洲票据常见字段：IBAN / BIC，从通用 OCR 全文中启发式提取（与美式 MICR 路径互补）。</summary>
internal static class CheckOcrEuInstrumentParser
{
    // 宽松匹配后按 mod-97 修剪尾部误吞的英文（如「…00 end」）；全文先转大写再匹配
    private static readonly Regex IbanLooseRegex = new(
        @"(?:^|[\s,;:])([A-Z]{2}[0-9]{2}(?:\s*[0-9A-Z]){11,48})",
        RegexOptions.Compiled);

    private static readonly Regex BicRegex = new(
        @"\b([A-Z]{4}[A-Z]{2}[A-Z0-9]{2}(?:[A-Z0-9]{3})?)\b",
        RegexOptions.Compiled);

    /// <summary>若全文含通过校验的 IBAN，返回去空格的标准形式；否则 null。</summary>
    public static string? TryFindValidIban(string? fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return null;

        var scan = fullText.ToUpperInvariant();
        foreach (Match m in IbanLooseRegex.Matches(scan))
        {
            var compact = CompactAlnum(m.Groups[1].Value);
            var resolved = TrimTrailingGarbageAndValidateIban(compact);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    /// <summary>去掉 OCR 常把英文单词粘在 IBAN 末尾的情况；仅当末尾为字母时逐步缩短再验 mod-97。</summary>
    private static string? TrimTrailingGarbageAndValidateIban(string rawUpper)
    {
        if (rawUpper.Length < 15)
            return null;

        var raw = rawUpper.Length > 34 ? rawUpper[..34] : rawUpper;
        while (raw.Length >= 15)
        {
            if (IsValidIbanMod97(raw))
                return raw;
            if (char.IsLetter(raw[^1]))
                raw = raw[..^1];
            else
                break;
        }

        return null;
    }

    /// <summary>返回首个形似 BIC 的 token（不做目录校验）。</summary>
    public static string? TryFindBic(string? fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return null;

        var m = BicRegex.Match(fullText);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string CompactAlnum(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToUpperInvariant();
    }

    /// <summary>ISO 13616 mod-97-10：重排后对 97 取模应得 1。</summary>
    public static bool IsValidIbanMod97(string ibanCompactUpper)
    {
        if (ibanCompactUpper.Length < 15 || ibanCompactUpper.Length > 34)
            return false;

        foreach (var c in ibanCompactUpper)
        {
            if (!char.IsLetterOrDigit(c))
                return false;
        }

        var r = string.Concat(ibanCompactUpper.AsSpan(4), ibanCompactUpper.AsSpan(0, 4));

        var expanded = new StringBuilder(r.Length * 2);
        foreach (var c in r)
        {
            if (c is >= '0' and <= '9')
                expanded.Append(c);
            else if (c is >= 'A' and <= 'Z')
                expanded.Append(c - 'A' + 10);
            else
                return false;
        }

        var rem = 0;
        foreach (var ch in expanded.ToString())
        {
            if (ch < '0' || ch > '9')
                return false;
            rem = (rem * 10 + (ch - '0')) % 97;
        }

        return rem == 1;
    }
}
