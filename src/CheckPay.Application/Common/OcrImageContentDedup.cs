using System.Security.Cryptography;
using System.Text.Json;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Application.Common;

/// <summary>上传页按图像字节哈希复用已完成 OCR，降低重复 Vision 调用。</summary>
public static class OcrImageContentDedup
{
    public static string ComputeSha256Hex(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>将 <paramref name="source"/> 上已成功识别的负载复制到 <paramref name="target"/>（新建行）。</summary>
    public static void CopyCompletedOcrFromSource(OcrResult target, OcrResult source)
    {
        target.RawResult?.Dispose();
        target.ConfidenceScores?.Dispose();
        target.AmountValidationResult?.Dispose();
        target.AzureRawResult?.Dispose();
        target.AzureConfidenceScores?.Dispose();

        target.RawResult = CloneJsonDocument(source.RawResult);
        target.ConfidenceScores = CloneJsonDocument(source.ConfidenceScores);
        target.AmountValidationResult = CloneJsonDocument(source.AmountValidationResult);
        target.AmountValidationStatus = source.AmountValidationStatus;
        target.AmountValidationErrorMessage = source.AmountValidationErrorMessage;
        target.AmountValidatedAt = source.AmountValidatedAt;

        target.Status = OcrStatus.Completed;
        target.ErrorMessage = null;
        target.RetryCount = 0;

        target.AzureRawResult = null;
        target.AzureConfidenceScores = null;
        target.AzureStatus = OcrStatus.Pending;
        target.AzureErrorMessage = null;
    }

    /// <summary>
    /// 查找可复用的已完成 OCR：同图 <paramref name="sha256Hex"/> 且库中至少有一条未软删的
    /// <see cref="CheckRecord"/>，其 <c>OcrResultId</c> 指向的 <see cref="OcrResult"/> 与该哈希一致（含仅保存草稿，<c>SubmittedAt</c> 可为空）。
    /// 返回其中最新创建的已完成行作为复制来源（宽语义：不要求该来源行本身挂有支票记录）。
    /// </summary>
    public static async Task<OcrResult?> FindReusableCompletedAsync(
        IApplicationDbContext db,
        string sha256Hex,
        int? maxSourceAgeDays,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sha256Hex)) return null;

        var hasCheckForImage = await (
            from c in db.CheckRecords.AsNoTracking()
            join o in db.OcrResults.AsNoTracking() on c.OcrResultId equals o.Id
            where o.ImageContentSha256 == sha256Hex
            select c).AnyAsync(cancellationToken);

        if (!hasCheckForImage)
            return null;

        var q = db.OcrResults.AsNoTracking()
            .Where(o =>
                o.ImageContentSha256 == sha256Hex
                && o.Status == OcrStatus.Completed
                && o.RawResult != null);

        if (maxSourceAgeDays is > 0)
        {
            var min = DateTime.UtcNow.Date.AddDays(-maxSourceAgeDays.Value);
            q = q.Where(o => o.CreatedAt >= min);
        }

        return await q
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static JsonDocument? CloneJsonDocument(JsonDocument? doc) =>
        doc == null ? null : JsonDocument.Parse(doc.RootElement.GetRawText());

    /// <summary>释放仅用于内存中复用的查询行上的 jsonb 文档（避免泄漏）。</summary>
    public static void DisposeDetachedPayload(OcrResult detachedRow)
    {
        detachedRow.RawResult?.Dispose();
        detachedRow.RawResult = null;
        detachedRow.ConfidenceScores?.Dispose();
        detachedRow.ConfidenceScores = null;
        detachedRow.AmountValidationResult?.Dispose();
        detachedRow.AmountValidationResult = null;
        detachedRow.AzureRawResult?.Dispose();
        detachedRow.AzureRawResult = null;
        detachedRow.AzureConfidenceScores?.Dispose();
        detachedRow.AzureConfidenceScores = null;
    }
}
