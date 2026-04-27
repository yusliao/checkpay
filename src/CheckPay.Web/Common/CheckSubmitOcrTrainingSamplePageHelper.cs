using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CheckPay.Web.Common;

/// <summary>上传页 / 复核页在支票最终入库成功后追加 OCR 训练样本。</summary>
public static class CheckSubmitOcrTrainingSamplePageHelper
{
    public static async Task TryAppendAfterCheckFinalSubmitAsync(
        IApplicationDbContext db,
        IConfiguration configuration,
        string imageUrl,
        Guid? ocrResultId,
        string submittedCheckNumber,
        decimal submittedAmount,
        DateTime submittedCheckDateUtc,
        CheckAchExtensionData submittedAch,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Ocr:Training:AutoSampleOnCheckSubmit", true))
            return;
        if (ocrResultId is null || ocrResultId == Guid.Empty)
            return;
        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        var ocrEntity = await db.OcrResults.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == ocrResultId.Value, cancellationToken);
        var doc = ocrEntity?.RawResult ?? ocrEntity?.AzureRawResult;
        if (doc == null)
            return;

        var requireDiff = configuration.GetValue("Ocr:Training:AutoSampleRequireDiff", true);
        var sample = SubmitCheckOcrTrainingSampleFactory.TryCreateFromCheckFinalSubmit(
            doc,
            imageUrl,
            submittedCheckNumber,
            submittedAmount,
            submittedCheckDateUtc,
            submittedAch,
            requireDiff);
        if (sample == null)
            return;

        try
        {
            db.OcrTrainingSamples.Add(sample);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // 支票已入库；训练样本为增强项，写入失败不阻断用户流程。
        }
    }
}
