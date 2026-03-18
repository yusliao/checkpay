using CheckPay.Application;
using CheckPay.Infrastructure;
using CheckPay.Infrastructure.Data;
using CheckPay.Worker.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
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
}

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

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
