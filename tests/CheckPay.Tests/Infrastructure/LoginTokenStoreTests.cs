using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class LoginTokenStoreTests
{
    [Fact]
    public void ConsumeToken_RoundTrip_PreservesLoginInfo()
    {
        var provider = new EphemeralDataProtectionProvider();
        var store = new LoginTokenStore(provider, NullLogger<LoginTokenStore>.Instance);
        var userId = Guid.NewGuid();
        var original = new LoginInfo(userId, "Display", "d@x.com", "Admin", "/records");

        var token = store.StoreLoginInfo(original);
        Assert.False(string.IsNullOrEmpty(token));

        var restored = store.ConsumeToken(token);
        Assert.NotNull(restored);
        Assert.Equal(userId, restored!.UserId);
        Assert.Equal("Display", restored.DisplayName);
        Assert.Equal("d@x.com", restored.Email);
        Assert.Equal("Admin", restored.Role);
        Assert.Equal("/records", restored.ReturnUrl);
    }

    [Fact]
    public void ConsumeToken_Garbage_ReturnsNull()
    {
        var provider = new EphemeralDataProtectionProvider();
        var store = new LoginTokenStore(provider, NullLogger<LoginTokenStore>.Instance);

        Assert.Null(store.ConsumeToken("not-a-valid-token"));
    }
}
