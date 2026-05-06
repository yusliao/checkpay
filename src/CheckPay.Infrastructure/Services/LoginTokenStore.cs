using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckPay.Application.Common.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 使用 DataProtection 包装短期登录载荷，不依赖进程内内存。
/// 解决：负载均衡/多副本下 Blazor 与 /Auth/SignIn 落到不同实例时，内存 token 丢失导致无法写 Cookie（表现为登录失败）。
/// 多机部署时需将 DataProtection 密钥持久化到**各实例共享**的目录（配置 <c>DataProtection:KeysDirectory</c> / 环境变量 <c>DATA_PROTECTION_KEYS_DIRECTORY</c>）。
/// </summary>
public sealed class LoginTokenStore : ILoginTokenStore
{
    private const string ProtectorPurpose = "CheckPay.LoginBridge.v1";
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    private readonly IDataProtector _protector;
    private readonly ILogger<LoginTokenStore> _logger;

    public LoginTokenStore(IDataProtectionProvider protectionProvider, ILogger<LoginTokenStore> logger)
    {
        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public string StoreLoginInfo(LoginInfo info)
    {
        var payload = new LoginBridgePayloadDto
        {
            UserId = info.UserId,
            DisplayName = info.DisplayName,
            Email = info.Email,
            Role = info.Role,
            ReturnUrl = string.IsNullOrEmpty(info.ReturnUrl) ? "/" : info.ReturnUrl,
            IssuedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        return Base64UrlEncode(protectedBytes);
    }

    public LoginInfo? ConsumeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var raw = Base64UrlDecode(token.Trim());
            var jsonBytes = _protector.Unprotect(raw);
            var payload = JsonSerializer.Deserialize<LoginBridgePayloadDto>(jsonBytes, JsonOptions);
            if (payload is null)
                return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var age = now - payload.IssuedUnix;
            if (age < 0 || age > (long)MaxAge.TotalSeconds)
                return null;

            return new LoginInfo(
                payload.UserId,
                payload.DisplayName,
                payload.Email,
                payload.Role,
                string.IsNullOrEmpty(payload.ReturnUrl) ? "/" : payload.ReturnUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "登录桥接 token 无效或解密失败");
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class LoginBridgePayloadDto
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public string ReturnUrl { get; set; } = "/";
        public long IssuedUnix { get; set; }
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
