using CheckPay.Domain.Common;
using CheckPay.Domain.Enums;
using System.Text.Json;

namespace CheckPay.Domain.Entities;

public class OcrResult : BaseEntity
{
    public string ImageUrl { get; set; } = string.Empty;
    public OcrStatus Status { get; set; } = OcrStatus.Pending;
    public JsonDocument? RawResult { get; set; }
    public JsonDocument? ConfidenceScores { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; } = 0;
    public AmountValidationStatus AmountValidationStatus { get; set; } = AmountValidationStatus.Pending;
    public JsonDocument? AmountValidationResult { get; set; }
    public string? AmountValidationErrorMessage { get; set; }
    public DateTime? AmountValidatedAt { get; set; }

    // 历史：双引擎并行时 Azure 结果存于此；当前生产仅使用 RawResult，新任务会清空下列字段
    public OcrStatus AzureStatus { get; set; } = OcrStatus.Pending;
    public JsonDocument? AzureRawResult { get; set; }
    public JsonDocument? AzureConfidenceScores { get; set; }
    public string? AzureErrorMessage { get; set; }
}
