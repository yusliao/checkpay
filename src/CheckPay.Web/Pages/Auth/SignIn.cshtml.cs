using CheckPay.Application.Common.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace CheckPay.Web.Pages.Auth;

/// <summary>
/// 登录 Cookie 写入端点（Razor Page，真实 HTTP 请求，可以安全写 Cookie）
/// Blazor 组件验证密码后 → 生成一次性 token → 跳转到这里 → 写 Cookie → 跳转首页
/// </summary>
public class SignInModel : PageModel
{
    private readonly ILoginTokenStore _tokenStore;

    public SignInModel(ILoginTokenStore tokenStore)
    {
        _tokenStore = tokenStore;
    }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return RedirectToPage("/login");

        var loginInfo = _tokenStore.ConsumeToken(token);
        if (loginInfo is null)
            return RedirectToPage("/login"); // token 无效或已过期

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, loginInfo.UserId.ToString()),
            new(ClaimTypes.Name, loginInfo.DisplayName),
            new(ClaimTypes.Email, loginInfo.Email),
            new(ClaimTypes.Role, loginInfo.Role)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // 跳转目标：优先返回原始请求页，否则去首页
        var returnUrl = loginInfo.ReturnUrl;
        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl))
            returnUrl = "/";

        return LocalRedirect(returnUrl);
    }
}
