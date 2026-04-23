using CheckPay.Domain.Common;
using CheckPay.Domain.Enums;

namespace CheckPay.Domain.Entities;

public class CheckRecord : BaseEntity
{
    public string CheckNumber { get; set; } = string.Empty;
    public decimal CheckAmount { get; set; }
    public DateTime CheckDate { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public CheckStatus Status { get; set; } = CheckStatus.PendingDebit;
    public string? ImageUrl { get; set; }
    public Guid? OcrResultId { get; set; }
    public OcrResult? OcrResult { get; set; }
    public string? Notes { get; set; }
    public uint RowVersion { get; set; }

    // ── ACH / 票面扩展 ───────────────────────────────────────────
    public string? BankName { get; set; }
    public string? RoutingNumber { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountType { get; set; }
    public string? AccountHolderName { get; set; }
    public string? AccountAddress { get; set; }
    public string? PayToOrderOf { get; set; }
    public string? ForMemo { get; set; }
    public string? MicrLineRaw { get; set; }
    public string? CheckNumberMicr { get; set; }

    /// <summary>支票关联的公司名称（付款/开票主体，可与客户主数据多名称比对）</summary>
    public string? CompanyName { get; set; }

    /// <summary>与客户主数据中已登记的公司名称均不匹配时置 true（提示新业务关系）</summary>
    public bool CustomerCompanyNewRelationshipWarning { get; set; }

    /// <summary>发票号，逗号分隔（产品约定：多发票用英文逗号）</summary>
    public string? InvoiceNumbers { get; set; }

    /// <summary>支付对应期间，如 2026-06</summary>
    public string? PaymentPeriodText { get; set; }

    /// <summary>销售/美财提交时间；null 表示草稿可编辑</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>与客户主数据期望银行/持有人不一致时置 true</summary>
    public bool CustomerMasterMismatchWarning { get; set; }

    /// <summary>美国侧 ACH 扣款是否已成功（由美财标记）</summary>
    public bool AchDebitSucceeded { get; set; }

    /// <summary>美财标记扣款成功的时间（UTC）</summary>
    public DateTime? AchDebitSucceededAt { get; set; }

    /// <summary>美财撤销「ACH 扣款成功」标记的时间（UTC）；用于大陆财务提示「银行侧扣款成功已被撤回」。</summary>
    public DateTime? AchDebitSuccessRevokedAt { get; set; }

    // 反向导航：关联的扣款记录
    public DebitRecord? DebitRecord { get; set; }
}
