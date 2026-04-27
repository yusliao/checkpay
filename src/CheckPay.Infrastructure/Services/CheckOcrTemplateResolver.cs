using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

public sealed class CheckOcrTemplateResolver(
    IApplicationDbContext dbContext,
    IMemoryCache memoryCache,
    ILogger<CheckOcrTemplateResolver> logger) : ICheckOcrTemplateResolver
{
    private const string CacheKey = "ocr_check_templates_active_v1";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public async Task<OcrTemplateResolution> ResolveAsync(
        string? routingDigits9,
        string extractedFullText,
        CancellationToken cancellationToken = default)
    {
        var templates = await LoadActiveTemplatesAsync(cancellationToken);
        if (templates.Count == 0)
            return new OcrTemplateResolution(null, null, CheckOcrParsingProfile.Default);

        var routing = NormDigits(routingDigits9);
        var lower = extractedFullText.ToLowerInvariant();

        foreach (var t in templates)
        {
            if (!MatchesRouting(t.RoutingPrefix, routing))
                continue;
            if (!MatchesKeywords(t.BankNameKeywords, lower))
                continue;

            var profile = CheckOcrParsingProfileSerializer.ParseMerged(t.ParsingProfileJson);
            logger.LogInformation(
                "支票 OCR 票型命中 TemplateId={TemplateId} Name={Name} RoutingPrefix={RoutingPrefix}",
                t.Id,
                t.Name,
                t.RoutingPrefix);

            return new OcrTemplateResolution(t.Id, t.Name, profile);
        }

        logger.LogInformation("支票 OCR 票型未命中，使用默认版式配置");
        return new OcrTemplateResolution(null, null, CheckOcrParsingProfile.Default);
    }

    private async Task<IReadOnlyList<OcrCheckTemplate>> LoadActiveTemplatesAsync(CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue(CacheKey, out IReadOnlyList<OcrCheckTemplate>? cached) && cached is not null)
            return cached;

        var rows = await dbContext.OcrCheckTemplates.AsNoTracking()
            .Where(t => t.IsActive && t.DeletedAt == null)
            .OrderByDescending(t => t.SortOrder)
            .ThenByDescending(t => t.RoutingPrefix != null ? t.RoutingPrefix.Length : 0)
            .ToListAsync(cancellationToken);

        memoryCache.Set(CacheKey, (IReadOnlyList<OcrCheckTemplate>)rows, CacheDuration);
        return rows;
    }

    private static bool MatchesRouting(string? routingPrefix, string? routingDigits)
    {
        if (string.IsNullOrWhiteSpace(routingPrefix))
            return true;

        var p = NormDigits(routingPrefix);
        if (string.IsNullOrEmpty(p))
            return true;

        if (string.IsNullOrEmpty(routingDigits))
            return false;

        return routingDigits.StartsWith(p, StringComparison.Ordinal);
    }

    private static bool MatchesKeywords(string? bankNameKeywords, string textLower)
    {
        if (string.IsNullOrWhiteSpace(bankNameKeywords))
            return true;

        var keys = bankNameKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0)
            return true;

        foreach (var k in keys)
        {
            if (!textLower.Contains(k, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string? NormDigits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }
}
