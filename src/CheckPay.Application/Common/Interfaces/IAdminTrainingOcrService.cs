namespace CheckPay.Application.Common.Interfaces;

/// <summary>
/// 管理端 OCR 训练标注使用的引擎：已配置 Azure Document Intelligence（Vision Read）时优先使用，
/// 否则与 <see cref="IOcrService"/> 一致（混元或 Mock）。
/// </summary>
public interface IAdminTrainingOcrService : IOcrService
{
}
