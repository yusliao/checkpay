using System.Text.Json;
using CheckPay.Application.Common.Models;

namespace CheckPay.Infrastructure.Services;

internal static class CheckOcrParsingProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static CheckOcrParsingProfile ParseMerged(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CheckOcrParsingProfile.Default;

        try
        {
            var partial = JsonSerializer.Deserialize<CheckOcrParsingProfile>(json, Options);
            return CheckOcrParsingProfile.MergeDefaults(partial);
        }
        catch
        {
            return CheckOcrParsingProfile.Default;
        }
    }
}
