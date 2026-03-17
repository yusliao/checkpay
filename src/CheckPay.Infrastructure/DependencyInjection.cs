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

        // OCR服务：有腾讯混元凭证时用 HunyuanOcrService，否则用 MockOcrService（开发/测试用）
        var hunyuanSecretId = configuration["Hunyuan:SecretId"];
        if (!string.IsNullOrWhiteSpace(hunyuanSecretId))
            services.AddScoped<IOcrService, HunyuanOcrService>();
        else
            services.AddScoped<IOcrService, MockOcrService>();

        // Blob存储服务：有 Azure Blob 配置就用 AzureBlobStorageService，默认走 Mock（开发/测试）
        var blobConnection = configuration["Azure:BlobStorage:ConnectionString"];
        var blobContainer = configuration["Azure:BlobStorage:ContainerName"];
        if (!string.IsNullOrWhiteSpace(blobConnection) && !string.IsNullOrWhiteSpace(blobContainer))
            services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
        else
            services.AddScoped<IBlobStorageService, MockBlobStorageService>();
        services.AddScoped<IAuditLogService, AuditLogService>();

        // 登录桥接服务：解决 Blazor Server 不能直接写 Cookie 的问题
        services.AddMemoryCache();
        services.AddSingleton<ILoginTokenStore, LoginTokenStore>();

        return services;
    }
}
