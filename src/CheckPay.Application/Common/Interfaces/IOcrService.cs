namespace CheckPay.Application.Common.Interfaces;

public interface IOcrService
{
    Task<OcrResultDto> ProcessCheckImageAsync(string imageUrl, CancellationToken cancellationToken = default);
    Task<DebitOcrResultDto> ProcessDebitImageAsync(string imageUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// 支票 OCR 结果：经典三字段 + ACH/MICR 扩展（空白或未识别时为 null 或默认值）
/// </summary>
public record OcrResultDto(
    string CheckNumber,
    decimal Amount,
    DateTime Date,
    Dictionary<string, double> ConfidenceScores,
    string? RoutingNumber = null,
    string? AccountNumber = null,
    string? BankName = null,
    string? AccountHolderName = null,
    string? AccountAddress = null,
    string? AccountType = null,
    string? PayToOrderOf = null,
    string? ForMemo = null,
    string? MicrLineRaw = null,
    string? CheckNumberMicr = null,
    string? MicrFieldOrderNote = null,
    /// <summary>票面公司名称 / 付款主体（常与 Pay to the order of 一致，可空）。</summary>
    string? CompanyName = null,
    /// <summary>通用 OCR 引擎抽取的全文（如 Azure Read），供训练页展示；混元等可为空。</summary>
    string? ExtractedText = null);

/// <summary>
/// 扣款凭证 OCR 识别结果，所有字段可空（识别失败时为 null，由财务手动填写）
/// </summary>
public record DebitOcrResultDto(
    string? CheckNumber,
    decimal? Amount,
    DateTime? Date,
    string? BankReference,
    Dictionary<string, double> ConfidenceScores,
    /// <summary>通用 OCR 引擎抽取的全文（如 Azure Read），供训练页展示。</summary>
    string? RawExtractedText = null);
