using System.Text.Json;

namespace CheckPay.Application.Common;

/// <summary>支票 <c>raw_result</c> JSON（根级 Vision 载荷）的简单读取。</summary>
public static class OcrResultRawJsonReaders
{
    public static string? TryReadExtractedText(JsonDocument? doc)
    {
        if (doc == null)
            return null;

        try
        {
            return doc.RootElement.TryGetProperty("ExtractedText", out var el)
                ? el.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
