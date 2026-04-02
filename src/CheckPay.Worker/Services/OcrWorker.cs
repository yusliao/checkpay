using System.Text.Json;
using System.Threading.Channels;
using CheckPay.Application.Common.Interfaces;
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

    public OcrWorker(ILogger<OcrWorker> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _channel = Channel.CreateUnbounded<Guid>();
        _maxRetries = int.TryParse(configuration["Ocr:MaxRetries"], out var r) ? r : 3;
        _retryDelayMs = int.TryParse(configuration["Ocr:RetryDelayMs"], out var d) ? d : 3000;
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
        _logger.LogInformation("OCR Worker 已启动（双引擎并行模式：混元 + Azure）");

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

        // 直接注入具体类型，两个引擎独立并行运行
        var hunyuanService = scope.ServiceProvider.GetRequiredService<HunyuanOcrService>();
        var azureService = scope.ServiceProvider.GetService<AzureOcrService>(); // 可选，未配置时为 null

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

        // Azure 未配置时直接标记跳过，不阻塞混元
        if (azureService == null)
        {
            ocrResult.AzureStatus = OcrStatus.Failed;
            ocrResult.AzureErrorMessage = "未配置 Azure Document Intelligence 凭证";
        }
        else
        {
            ocrResult.AzureStatus = OcrStatus.Processing;
        }

        await dbContext.SaveChangesAsync(stoppingToken);

        // ── 并行调用两个引擎 ──────────────────────────────────
        var hunyuanTask = hunyuanService.ProcessCheckImageAsync(ocrResult.ImageUrl, stoppingToken);
        var azureTask = azureService != null
            ? azureService.ProcessCheckImageAsync(ocrResult.ImageUrl, stoppingToken)
            : Task.FromResult<OcrResultDto?>(null)!;

        // 等待两个任务都完成（无论成功失败）
        await Task.WhenAll(
            hunyuanTask.ContinueWith(_ => { }, stoppingToken),
            azureTask.ContinueWith(_ => { }, stoppingToken));

        // ── 写入混元结果 ──────────────────────────────────────
        if (hunyuanTask.IsCompletedSuccessfully)
        {
            var result = hunyuanTask.Result;
            ocrResult.RawResult?.Dispose();
            ocrResult.ConfidenceScores?.Dispose();
            ocrResult.RawResult = JsonDocument.Parse(JsonSerializer.Serialize(result));
            ocrResult.ConfidenceScores = JsonDocument.Parse(JsonSerializer.Serialize(result.ConfidenceScores));
            ocrResult.Status = OcrStatus.Completed;
        }
        else
        {
            var ex = hunyuanTask.Exception?.InnerException ?? hunyuanTask.Exception;
            _logger.LogError(ex, "混元 OCR 识别失败: {OcrResultId}", ocrResultId);

            var isTransient = IsTransientException(ex);
            if (isTransient && ocrResult.RetryCount < _maxRetries)
            {
                ocrResult.RetryCount++;
                ocrResult.Status = OcrStatus.Pending;
                ocrResult.ErrorMessage = $"第 {ocrResult.RetryCount} 次重试，原因: {ex?.Message}";
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
            ocrResult.ErrorMessage = ex?.Message;
        }

        // ── 写入 Azure 结果 ───────────────────────────────────
        if (azureService != null)
        {
            if (azureTask.IsCompletedSuccessfully)
            {
                var result = azureTask.Result;
                ocrResult.AzureRawResult?.Dispose();
                ocrResult.AzureConfidenceScores?.Dispose();
                ocrResult.AzureRawResult = JsonDocument.Parse(JsonSerializer.Serialize(result));
                ocrResult.AzureConfidenceScores = JsonDocument.Parse(JsonSerializer.Serialize(result.ConfidenceScores));
                ocrResult.AzureStatus = OcrStatus.Completed;
            }
            else
            {
                var ex = azureTask.Exception?.InnerException ?? azureTask.Exception;
                _logger.LogError(ex, "Azure OCR 识别失败: {OcrResultId}", ocrResultId);
                ocrResult.AzureStatus = OcrStatus.Failed;
                ocrResult.AzureErrorMessage = ex?.Message;
            }
        }

        ocrResult.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(stoppingToken);
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
            // 空响应或空文件可重试，MinIO 下载失败属于瞬态
            return msg.Contains("空响应") || msg.Contains("空文件") || msg.Contains("MinIO下载失败");
        }

        return false;
    }
}
