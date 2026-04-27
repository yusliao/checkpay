using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Infrastructure.Services;

/// <summary>支票训练样本：差异判断与有序加载（few-shot / Azure 纠偏共用）。</summary>
internal static class CheckOcrTrainingSampleDiff
{
    public static bool HasStructuredDiff(OcrTrainingSample s)
    {
        if (!string.Equals(Norm(s.OcrCheckNumber), Norm(s.CorrectCheckNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.OcrAmount != s.CorrectAmount)
            return true;
        var od = s.OcrDate?.Date;
        var cd = s.CorrectDate?.Date;
        if (od != cd)
            return true;

        var oAch = CheckAchExtensionData.Deserialize(s.OcrAchExtensionJson);
        var cAch = CheckAchExtensionData.Deserialize(s.CorrectAchExtensionJson);
        if (oAch == null && cAch == null)
            return false;
        if (oAch == null || cAch == null)
            return true;

        return !CheckAchExtensionData.EqualsForTraining(oAch, cAch);
    }

    public static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public static string? NormDigits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }
}

internal static class CheckOcrTrainingSamplePool
{
    public const int DefaultPoolTake = 80;

    public static async Task<List<OcrTrainingSample>> LoadOrderedAsync(
        IApplicationDbContext db,
        int poolTake,
        int maxReturn,
        CancellationToken cancellationToken)
    {
        var pool = await db.OcrTrainingSamples.AsNoTracking()
            .Where(s => s.DocumentType == "check")
            .OrderByDescending(s => s.CreatedAt)
            .Take(poolTake)
            .ToListAsync(cancellationToken);

        return pool
            .OrderByDescending(s => CheckOcrTrainingSampleDiff.HasStructuredDiff(s) ? 1 : 0)
            .ThenByDescending(s => s.CreatedAt)
            .Take(maxReturn)
            .ToList();
    }
}
