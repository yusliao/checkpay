using System.IO;
using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
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

        var dpKeysDir = configuration["DataProtection:KeysDirectory"]
            ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(dpKeysDir))
            dpKeysDir = Path.Combine(AppContext.BaseDirectory, "data-protection-keys");
        Directory.CreateDirectory(dpKeysDir);

        services.AddDataProtection()
            .SetApplicationName("CheckPay")
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
            }));

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());

        services.AddMemoryCache();

        // OCR：Azure AI Vision Read（支票/扣款）；未配置时使用 Mock（Worker 会标记失败）
        services.AddScoped<ICheckOcrParsedSampleCorrector, CheckOcrParsedSampleCorrector>();
        services.AddScoped<ICheckOcrTemplateResolver, CheckOcrTemplateResolver>();

        var azureEndpoint = configuration["Azure:DocumentIntelligence:Endpoint"];
        var azureApiKey = configuration["Azure:DocumentIntelligence:ApiKey"];
        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureApiKey)
            && !azureEndpoint.Contains("your-resource"))
        {
            services.AddScoped<AzureOcrService>();
            services.AddScoped<IOcrService>(sp => sp.GetRequiredService<AzureOcrService>());
        }
        else
        {
            services.AddScoped<IOcrService, MockOcrService>();
        }

        services.AddScoped<IAdminTrainingOcrService>(sp =>
            new AdminTrainingOcrService(sp.GetRequiredService<IOcrService>()));

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

        // 登录桥接：DataProtection 封装短期载荷（非内存），多副本/负载均衡下仍可用
        services.AddSingleton<ILoginTokenStore, LoginTokenStore>();

        return services;
    }
}
