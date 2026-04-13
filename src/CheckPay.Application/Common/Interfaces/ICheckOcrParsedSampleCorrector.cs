namespace CheckPay.Application.Common.Interfaces;

/// <summary>
/// 将支票 OCR 解析结果与训练样本中「模型曾错、人工已改」的记录做结构化匹配，匹配则合并核定字段（供 Azure 等非 Prompt 引擎使用）。
/// </summary>
public interface ICheckOcrParsedSampleCorrector
{
    /// <summary>若命中训练样本则返回纠偏后的 DTO，否则原样返回。</summary>
    Task<OcrResultDto> ApplyIfMatchedAsync(OcrResultDto parsed, CancellationToken cancellationToken = default);
}
