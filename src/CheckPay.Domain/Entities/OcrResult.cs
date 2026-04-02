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

    // Azure Document Intelligence OCR 识别结果（与混元并行，用于比对）
    public OcrStatus AzureStatus { get; set; } = OcrStatus.Pending;
    public JsonDocument? AzureRawResult { get; set; }
    public JsonDocument? AzureConfidenceScores { get; set; }
    public string? AzureErrorMessage { get; set; }
}
