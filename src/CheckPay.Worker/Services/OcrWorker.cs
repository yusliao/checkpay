using System.Text.Json;
using System.Threading.Channels;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CheckPay.Worker.Services;

public class OcrWorker : BackgroundService
{
    private readonly ILogger<OcrWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<Guid> _channel;

    public OcrWorker(ILogger<OcrWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _channel = Channel.CreateUnbounded<Guid>();
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
        _logger.LogInformation("OCR Worker 已启动");

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
        var ocrService = scope.ServiceProvider.GetRequiredService<IOcrService>();

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

        try
        {
            _logger.LogInformation("处理 OCR 任务: {OcrResultId}", ocrResultId);
            ocrResult.Status = OcrStatus.Processing;
            ocrResult.ErrorMessage = null;
            ocrResult.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(stoppingToken);

            var result = await ocrService.ProcessCheckImageAsync(ocrResult.ImageUrl, stoppingToken);

            ocrResult.RawResult?.Dispose();
            ocrResult.ConfidenceScores?.Dispose();
            ocrResult.RawResult = JsonDocument.Parse(JsonSerializer.Serialize(result));
            ocrResult.ConfidenceScores = JsonDocument.Parse(JsonSerializer.Serialize(result.ConfidenceScores));
            ocrResult.Status = OcrStatus.Completed;
            ocrResult.UpdatedAt = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 OCR 任务失败: {OcrResultId}", ocrResultId);
            ocrResult.Status = OcrStatus.Failed;
            ocrResult.ErrorMessage = ex.Message;
            ocrResult.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(stoppingToken);
        }
    }
}
