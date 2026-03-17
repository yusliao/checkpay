using CheckPay.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace CheckPay.Infrastructure.Services;

/// <summary>
/// 基于内存缓存的一次性登录 token 存储
/// token 30秒后自动过期，ConsumeToken 调用后立即删除，确保一次性使用
/// </summary>
public class LoginTokenStore : ILoginTokenStore
{
    private readonly IMemoryCache _cache;

    public LoginTokenStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public string StoreLoginInfo(LoginInfo info)
    {
        var token = Guid.NewGuid().ToString("N"); // 32位随机 token，无法猜测
        _cache.Set(CacheKey(token), info, TimeSpan.FromSeconds(30));
        return token;
    }

    public LoginInfo? ConsumeToken(string token)
    {
        var key = CacheKey(token);
        if (!_cache.TryGetValue(key, out LoginInfo? info))
            return null;

        _cache.Remove(key); // 用完即删，真正一次性
        return info;
    }

    private static string CacheKey(string token) => $"login_token:{token}";
}
