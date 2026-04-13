using CheckPay.Domain.Common;

namespace CheckPay.Domain.Entities;

/// <summary>
/// OCR 训练样本：记录图片的 OCR 识别结果与人工标注的正确值，用于分析模型弱点、优化 Prompt
/// </summary>
public class OcrTrainingSample : BaseEntity
{
    /// <summary>上传到 MinIO 的图片 URL</summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>文档类型：check / debit</summary>
    public string DocumentType { get; set; } = "debit";

    /// <summary>模型返回的原始文本（含解释性文字时也完整保存）</summary>
    public string OcrRawResponse { get; set; } = string.Empty;

    // ── OCR 自动解析结果 ──────────────────────────────────────────
    public string? OcrCheckNumber { get; set; }
    public decimal? OcrAmount { get; set; }
    public DateTime? OcrDate { get; set; }
    public string? OcrBankReference { get; set; }

    // ── 人工标注正确值 ─────────────────────────────────────────────
    public string? CorrectCheckNumber { get; set; }
    public decimal? CorrectAmount { get; set; }
    public DateTime? CorrectDate { get; set; }
    public string? CorrectBankReference { get; set; }

    /// <summary>备注（如图片来源、特殊格式说明）</summary>
    public string? Notes { get; set; }

    /// <summary>支票 ACH 扩展字段 OCR 快照（JSON，与 OcrResultDto 扩展字段对齐）</summary>
    public string? OcrAchExtensionJson { get; set; }

    /// <summary>支票 ACH 扩展字段人工标注（JSON）</summary>
    public string? CorrectAchExtensionJson { get; set; }
}
