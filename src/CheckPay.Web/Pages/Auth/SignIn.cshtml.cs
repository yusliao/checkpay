using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CheckPay.Web.Pages.Auth;

/// <summary>
/// 登录 Cookie 写入端点（Razor Page，真实 HTTP 请求，可以安全写 Cookie）
/// Blazor 组件验证密码后 → 生成一次性 token → 跳转到这里 → 写 Cookie → 跳转首页
/// </summary>
public class SignInModel : PageModel
{
    private readonly ILoginTokenStore _tokenStore;
    private readonly IServiceScopeFactory _scopeFactory;

    public SignInModel(ILoginTokenStore tokenStore, IServiceScopeFactory scopeFactory)
    {
        _tokenStore = tokenStore;
        _scopeFactory = scopeFactory;
    }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return LocalRedirect("/login");

        var loginInfo = _tokenStore.ConsumeToken(token);
        if (loginInfo is null)
            return LocalRedirect("/login");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == loginInfo.UserId && u.IsActive);
        if (user is null)
            return LocalRedirect("/login");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        var returnUrl = loginInfo.ReturnUrl;
        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl))
            returnUrl = "/";

        return LocalRedirect(returnUrl);
    }
}
