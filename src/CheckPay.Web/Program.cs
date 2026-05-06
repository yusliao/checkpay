using CheckPay.Application;
using CheckPay.Infrastructure;
using CheckPay.Infrastructure.Data;
using CheckPay.Worker.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// 工作目录若非 publish 目录（systemd、计划任务、误用 dotnet 全路径启动等），默认 ContentRoot 会指错，
// 导致 wwwroot/_framework/blazor.server.js 等静态资源 404。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton<OcrWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OcrWorker>());

var app = builder.Build();

// 自动执行数据库迁移和种子数据（带重试机制）
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // 等待数据库可用（最多重试10次，每次等待3秒）
    var retryCount = 0;
    var maxRetries = 10;
    while (retryCount < maxRetries)
    {
        try
        {
            await dbContext.Database.MigrateAsync();
            logger.LogInformation("数据库迁移成功");
            break;
        }
        catch (Exception ex)
        {
            retryCount++;
            if (retryCount >= maxRetries)
            {
                logger.LogError(ex, "数据库连接失败，已达到最大重试次数");
                throw;
            }
            logger.LogWarning($"数据库连接失败，{3}秒后重试 ({retryCount}/{maxRetries})...");
            await Task.Delay(3000);
        }
    }

    // 初始化默认用户（如果不存在）
    if (!await dbContext.Users.AnyAsync())
    {
        var defaultUsers = new[]
        {
            new CheckPay.Domain.Entities.User
            {
                Id = Guid.NewGuid(),
                Email = "admin@checkpay.local",
                DisplayName = "系统管理员",
                Role = CheckPay.Domain.Enums.UserRole.Admin,
                EntraId = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CheckPay.Domain.Entities.User
            {
                Id = Guid.NewGuid(),
                Email = "sales@checkpay.local",
                DisplayName = "销售",
                Role = CheckPay.Domain.Enums.UserRole.Sales,
                EntraId = "sales",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("sales123"),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CheckPay.Domain.Entities.User
            {
                Id = Guid.NewGuid(),
                Email = "usfinance@checkpay.local",
                DisplayName = "美国财务",
                Role = CheckPay.Domain.Enums.UserRole.USFinance,
                EntraId = "usfinance",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("usfinance123"),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CheckPay.Domain.Entities.User
            {
                Id = Guid.NewGuid(),
                Email = "cnfinance@checkpay.local",
                DisplayName = "大陆财务",
                Role = CheckPay.Domain.Enums.UserRole.CNFinance,
                EntraId = "cnfinance",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("cnfinance123"),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };
        dbContext.Users.AddRange(defaultUsers);
        await dbContext.SaveChangesAsync();
    }
    else if (!await dbContext.Users.AnyAsync(u => u.EntraId == "sales"))
    {
        dbContext.Users.Add(new CheckPay.Domain.Entities.User
        {
            Id = Guid.NewGuid(),
            Email = "sales@checkpay.local",
            DisplayName = "销售",
            Role = CheckPay.Domain.Enums.UserRole.Sales,
            EntraId = "sales",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("sales123"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        logger.LogInformation("已补充默认销售账号 sales（历史库无此用户时自动创建）");
    }
}

// Blazor Server 预渲染用 Request 构造 NavigationManager.BaseUri；Host 为空、仅端口、或主机名不符合 URI 规则（如部分内网名下划线）时会抛出 UriFormatException。将 Host 改为可解析形式，避免整页 500。
app.Use(async (context, next) =>
{
    var request = context.Request;
    var hostPart = request.Host.Host;
    var needsFix = string.IsNullOrWhiteSpace(hostPart);
    if (!needsFix)
    {
        try
        {
            var pathBase = request.PathBase.HasValue ? request.PathBase.Value! : string.Empty;
            _ = new Uri($"{request.Scheme}://{request.Host.Value}{pathBase}/", UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            needsFix = true;
        }
    }

    if (needsFix)
    {
        var port = request.Host.Port;
        request.Headers.Host = port is > 0 and not 80 and not 443
            ? $"127.0.0.1:{port}"
            : "127.0.0.1";
    }

    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// app.UseHttpsRedirection(); // 临时注释掉，避免HTTPS证书问题
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
