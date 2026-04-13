using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;

namespace CheckPay.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("数据库连接字符串未配置");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
            }));

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());

        // OCR 服务（双引擎并行：混元 + Azure，用于比对评估）
        // 混元：有凭证时注册具体类型，否则用 Mock 兜底
        var hunyuanSecretId = configuration["Hunyuan:SecretId"];
        services.AddScoped<ICheckOcrFewShotProvider, CheckOcrFewShotProvider>();
        services.AddScoped<ICheckOcrParsedSampleCorrector, CheckOcrParsedSampleCorrector>();

        if (!string.IsNullOrWhiteSpace(hunyuanSecretId))
        {
            services.AddScoped<HunyuanOcrService>();
        }
        else
        {
            // 开发环境无凭证时，用工厂将 MockOcrService 适配为 HunyuanOcrService 的替代
            services.AddScoped<HunyuanOcrService>(sp =>
                throw new InvalidOperationException("混元凭证未配置，请检查 Hunyuan:SecretId 配置"));
            services.AddScoped<IOcrService, MockOcrService>();
        }

        // Azure：有凭证时注册具体类型（可选，未配置时 OcrWorker 跳过 Azure 引擎）
        var azureEndpoint = configuration["Azure:DocumentIntelligence:Endpoint"];
        var azureApiKey = configuration["Azure:DocumentIntelligence:ApiKey"];
        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureApiKey)
            && !azureEndpoint.Contains("your-resource"))
        {
            services.AddScoped<AzureOcrService>();
        }

        // IOcrService 指向混元（供非 Worker 场景使用，如 OcrTraining 页面）
        if (!string.IsNullOrWhiteSpace(hunyuanSecretId))
            services.AddScoped<IOcrService>(sp => sp.GetRequiredService<HunyuanOcrService>());

        // 管理端 OCR 训练标注：已配置 Azure 时优先用 Vision Read，否则沿用 IOcrService
        services.AddScoped<IAdminTrainingOcrService>(sp =>
        {
            var azure = sp.GetService<AzureOcrService>();
            IOcrService primary = azure ?? sp.GetRequiredService<IOcrService>();
            return new AdminTrainingOcrService(primary);
        });

        // Blob存储服务：优先 MinIO > Azure > Mock
        var minioEndpoint = configuration["Minio:Endpoint"];
        var minioAccessKey = configuration["Minio:AccessKey"];
        var minioSecretKey = configuration["Minio:SecretKey"];
        var minioBucketName = configuration["Minio:BucketName"];

        if (!string.IsNullOrWhiteSpace(minioEndpoint) &&
            !string.IsNullOrWhiteSpace(minioAccessKey) &&
            !string.IsNullOrWhiteSpace(minioSecretKey) &&
            !string.IsNullOrWhiteSpace(minioBucketName))
        {
            services.AddScoped<IBlobStorageService, MinioStorageService>();
        }
        else
        {
            var blobConnection = configuration["Azure:BlobStorage:ConnectionString"];
            var blobContainer = configuration["Azure:BlobStorage:ContainerName"];
            if (!string.IsNullOrWhiteSpace(blobConnection) && !string.IsNullOrWhiteSpace(blobContainer))
                services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
            else
                services.AddScoped<IBlobStorageService, MockBlobStorageService>();
        }
        services.AddScoped<IAuditLogService, AuditLogService>();

        // 登录桥接服务：解决 Blazor Server 不能直接写 Cookie 的问题
        services.AddMemoryCache();
        services.AddSingleton<ILoginTokenStore, LoginTokenStore>();

        return services;
    }
}
