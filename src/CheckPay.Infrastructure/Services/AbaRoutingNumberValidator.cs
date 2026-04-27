namespace CheckPay.Infrastructure.Services;

/// <summary>美国 ABA 路由号（9 位）校验位（mod 10），用于过滤 OCR 误识别的 9 位数字串。</summary>
internal static class AbaRoutingNumberValidator
{
    /// <summary>9 位全数字且校验位正确时返回 true。</summary>
    public static bool IsValid(ReadOnlySpan<char> nineDigits)
    {
        if (nineDigits.Length != 9)
            return false;

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var c = nineDigits[i];
            if (c < '0' || c > '9')
                return false;
            var d = c - '0';
            sum += (i % 3) switch
            {
                0 => 3 * d,
                1 => 7 * d,
                _ => d
            };
        }

        return sum % 10 == 0;
    }

    public static bool IsValid(string? nineDigits)
    {
        if (string.IsNullOrEmpty(nineDigits) || nineDigits.Length != 9)
            return false;
        return IsValid(nineDigits.AsSpan());
    }
}
