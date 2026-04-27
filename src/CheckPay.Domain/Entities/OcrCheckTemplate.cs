using CheckPay.Domain.Common;

namespace CheckPay.Domain.Entities;

/// <summary>支票 OCR 票型：路由前缀/关键字 + 版式 JSON，用于 Vision Read 后处理区域加权。</summary>
public class OcrCheckTemplate : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>9 位路由号前缀；为空表示不匹配路由（仅靠关键字或低优先级）。</summary>
    public string? RoutingPrefix { get; set; }

    /// <summary>逗号分隔关键字，须全部出现在 OCR 全文（忽略大小写）。</summary>
    public string? BankNameKeywords { get; set; }

    /// <summary>JSON 版式配置（与 Application 层 CheckOcrParsingProfile 字段一致）。</summary>
    public string? ParsingProfileJson { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>越大越优先；同序时更长 RoutingPrefix 优先。</summary>
    public int SortOrder { get; set; }
}
