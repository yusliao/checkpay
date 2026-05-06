using System.Security.Cryptography;
using System.Text.Json;
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

    public static async Task<OcrResult?> FindReusableCompletedAsync(
        IQueryable<OcrResult> ocrResultsNoTracking,
        string sha256Hex,
        int? maxSourceAgeDays,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sha256Hex)) return null;

        var q = ocrResultsNoTracking
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
