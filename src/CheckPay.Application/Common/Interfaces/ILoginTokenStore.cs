namespace CheckPay.Application.Common.Interfaces;

/// <summary>
/// Blazor Server 登录桥接服务
/// 解决 Blazor Server 无法直接调用 HttpContext.SignInAsync 的问题：
/// Blazor 组件验证密码后，存入短期 token → 跳转到 Razor Page → Razor Page 换取用户信息写 Cookie
/// </summary>
public interface ILoginTokenStore
{
    /// <summary>存储登录用户信息，返回一次性 token（30秒有效）</summary>
    string StoreLoginInfo(LoginInfo info);

    /// <summary>消费 token，获取登录信息（用完即删）</summary>
    LoginInfo? ConsumeToken(string token);
}

public record LoginInfo(
    Guid UserId,
    string DisplayName,
    string Email,
    string Role,
    string ReturnUrl = "/");
