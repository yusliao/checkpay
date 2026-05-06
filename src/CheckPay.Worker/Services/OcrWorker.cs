using System.Text.Json;
using System.Threading.Channels;
using CheckPay.Application.Common;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CheckPay.Worker.Services;

public class OcrWorker : BackgroundService
{
    private readonly ILogger<OcrWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<Guid> _channel;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;
    private readonly bool _amountValidationEnabled;
    private readonly bool _amountValidationFailOpen;
    private readonly double _amountValidationTriggerConfidenceThreshold;

    public OcrWorker(ILogger<OcrWorker> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _channel = Channel.CreateUnbounded<Guid>();
        _maxRetries = int.TryParse(configuration["Ocr:MaxRetries"], out var r) ? r : 3;
        _retryDelayMs = int.TryParse(configuration["Ocr:RetryDelayMs"], out var d) ? d : 3000;
        _amountValidationEnabled = bool.TryParse(configuration["Ocr:AmountValidation:Enabled"], out var enabled) && enabled;
        _amountValidationFailOpen = !bool.TryParse(configuration["Ocr:AmountValidation:FailOpen"], out var failOpen) || failOpen;
        _amountValidationTriggerConfidenceThreshold =
            double.TryParse(configuration["Ocr:AmountValidation:TriggerConfidenceThreshold"], out var threshold)
                ? threshold
                : 0.85;
    }

    public Task EnqueueAsync(Guid ocrResultId, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(ocrResultId, cancellationToken).AsTask();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnqueuePendingJobsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OCR Worker 已启动（Azure AI Vision Read）");

        await foreach (var ocrResultId in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(ocrResultId, stoppingToken);
        }
    }

    private async Task EnqueuePendingJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var pendingIds = await dbContext.OcrResults
            .Where(o => o.Status == OcrStatus.Pending || o.Status == OcrStatus.Processing)
            .OrderBy(o => o.CreatedAt)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in pendingIds)
        {
            await _channel.Writer.WriteAsync(id, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(Guid ocrResultId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var azureService = scope.ServiceProvider.GetService<AzureOcrService>();

        var ocrResult = await dbContext.OcrResults.FirstOrDefaultAsync(o => o.Id == ocrResultId, stoppingToken);
        if (ocrResult == null)
        {
            _logger.LogWarning("未找到 OCR 记录: {OcrResultId}", ocrResultId);
            return;
        }

        if (ocrResult.Status == OcrStatus.Completed)
        {
            _logger.LogDebug("OCR 记录 {OcrResultId} 已完成，跳过", ocrResultId);
            return;
        }

        _logger.LogInformation("处理 OCR 任务: {OcrResultId}，第 {Attempt} 次", ocrResultId, ocrResult.RetryCount + 1);
        ocrResult.Status = OcrStatus.Processing;
        ocrResult.ErrorMessage = null;
        ocrResult.UpdatedAt = DateTime.UtcNow;

        if (azureService == null)
        {
            ocrResult.Status = OcrStatus.Failed;
            ocrResult.ErrorMessage =
                "Azure Document Intelligence（Vision Read）未配置。请设置 Azure:DocumentIntelligence:Endpoint 与 ApiKey。";
            await dbContext.SaveChangesAsync(stoppingToken);
            return;
        }

        await dbContext.SaveChangesAsync(stoppingToken);

        try
        {
            var result = await azureService.ProcessCheckImageAsync(ocrResult.ImageUrl, stoppingToken);

            ocrResult.RawResult?.Dispose();
            ocrResult.ConfidenceScores?.Dispose();
            ocrResult.RawResult = JsonDocument.Parse(JsonSerializer.Serialize(result));
            ocrResult.ConfidenceScores = JsonDocument.Parse(JsonSerializer.Serialize(result.ConfidenceScores));
            ocrResult.Status = OcrStatus.Completed;
            await RunAmountValidationAsync(azureService, result, ocrResult, stoppingToken);
            PatchRawResultWithAmountValidationDiagnostics(ocrResult);

            ocrResult.AzureRawResult?.Dispose();
            ocrResult.AzureRawResult = null;
            ocrResult.AzureConfidenceScores?.Dispose();
            ocrResult.AzureConfidenceScores = null;
            ocrResult.AzureStatus = OcrStatus.Pending;
            ocrResult.AzureErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure OCR 识别失败: {OcrResultId}", ocrResultId);
            var isTransient = IsTransientException(ex);
            if (isTransient && ocrResult.RetryCount < _maxRetries)
            {
                ocrResult.RetryCount++;
                ocrResult.Status = OcrStatus.Pending;
                ocrResult.ErrorMessage = $"第 {ocrResult.RetryCount} 次重试，原因: {ex.Message}";
                ocrResult.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(stoppingToken);

                var delay = _retryDelayMs * ocrResult.RetryCount;
                _logger.LogWarning("OCR 任务 {OcrResultId} 将在 {Delay}ms 后重试（第 {RetryCount}/{MaxRetries} 次）",
                    ocrResultId, delay, ocrResult.RetryCount, _maxRetries);
                await Task.Delay(delay, stoppingToken);
                await _channel.Writer.WriteAsync(ocrResult.Id, stoppingToken);
                return;
            }

            ocrResult.Status = OcrStatus.Failed;
            ocrResult.ErrorMessage = ex.Message;
        }

        ocrResult.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(stoppingToken);
    }

    private async Task RunAmountValidationAsync(
        AzureOcrService azureService,
        OcrResultDto ocr,
        OcrResult entity,
        CancellationToken cancellationToken)
    {
        entity.AmountValidationResult?.Dispose();
        entity.AmountValidationResult = null;
        entity.AmountValidationErrorMessage = null;
        entity.AmountValidatedAt = DateTime.UtcNow;

        if (!_amountValidationEnabled)
        {
            entity.AmountValidationStatus = AmountValidationStatus.Skipped;
            return;
        }

        if (ocr.Amount <= 0m)
        {
            entity.AmountValidationStatus = AmountValidationStatus.Skipped;
            entity.AmountValidationErrorMessage = "OCR 未识别到有效数字金额";
            return;
        }

        var amountConfidence = ocr.ConfidenceScores.TryGetValue("Amount", out var conf) ? conf : 0.0;
        if (amountConfidence >= _amountValidationTriggerConfidenceThreshold
            && !WeakCourtesyAmountParseModeOcrStillRunValidation(ocr))
        {
            entity.AmountValidationStatus = AmountValidationStatus.Skipped;
            entity.AmountValidationErrorMessage = $"金额置信度 {amountConfidence:F2} 高于阈值，跳过二次校验";
            return;
        }

        try
        {
            var validation = await azureService.ValidateHandwrittenAmountAsync(
                entity.ImageUrl,
                ocr.Amount,
                cancellationToken,
                ocr.ExtractedText);
            entity.AmountValidationResult = JsonDocument.Parse(JsonSerializer.Serialize(validation));
            entity.AmountValidationStatus = validation.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                ? AmountValidationStatus.Completed
                : validation.Status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
                    ? AmountValidationStatus.Skipped
                    : AmountValidationStatus.Failed;
            entity.AmountValidationErrorMessage = validation.Reason;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "金额二次校验失败: {OcrResultId}", entity.Id);
            entity.AmountValidationStatus = AmountValidationStatus.Failed;
            entity.AmountValidationErrorMessage = ex.Message;
            if (!_amountValidationFailOpen)
                throw;
        }
    }

    private static void PatchRawResultWithAmountValidationDiagnostics(OcrResult entity)
    {
        if (entity.RawResult is null)
            return;
        var merged = OcrRawResultAmountValidationDiagnostics.MergeIntoRawJson(
            entity.RawResult,
            entity.AmountValidationResult,
            entity.AmountValidationStatus,
            entity.AmountValidationErrorMessage);
        entity.RawResult.Dispose();
        entity.RawResult = merged;
    }

    private static bool WeakCourtesyAmountParseModeOcrStillRunValidation(OcrResultDto ocr)
    {
        if (ocr.Diagnostics is null)
            return false;
        if (!ocr.Diagnostics.TryGetValue("amount_parse_mode", out var mode) || string.IsNullOrWhiteSpace(mode))
            return false;
        return mode is "space_cents_inline" or "spillover_cents" or "newline_cents"
               or "fraction_100" or "spillover_fraction_100" or "newline_fraction_100";
    }

    /// <summary>
    /// 判断是否为瞬态异常（网络抖动、超时、空响应），可以重试
    /// 401/403 权限错误和 MinIO 资源不存在属于不可重试异常
    /// </summary>
    private static bool IsTransientException(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is HttpRequestException or TaskCanceledException)
            return true;

        if (ex is InvalidOperationException ioe)
        {
            var msg = ioe.Message;
            return msg.Contains("空响应") || msg.Contains("空文件") || msg.Contains("MinIO下载失败");
        }

        return false;
    }
}
