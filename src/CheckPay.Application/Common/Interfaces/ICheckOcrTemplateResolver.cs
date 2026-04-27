using CheckPay.Application.Common.Models;

namespace CheckPay.Application.Common.Interfaces;

public sealed record OcrTemplateResolution(Guid? TemplateId, string? TemplateName, CheckOcrParsingProfile Profile);

/// <summary>根据路由号与全文关键字选择支票 OCR 版式配置（票型）。</summary>
public interface ICheckOcrTemplateResolver
{
    Task<OcrTemplateResolution> ResolveAsync(
        string? routingDigits9,
        string extractedFullText,
        CancellationToken cancellationToken = default);
}
