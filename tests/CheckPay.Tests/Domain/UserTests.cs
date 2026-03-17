using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

namespace CheckPay.Tests.Domain;

public class UserTests
{
    [Fact]
    public void User_ShouldInitializeWithDefaultValues()
    {
        var user = new User();

        Assert.Equal(string.Empty, user.Email);
        Assert.Equal(string.Empty, user.DisplayName);
        Assert.Equal(string.Empty, user.EntraId);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void User_ShouldSetProperties()
    {
        var user = new User
        {
            Email = "test@example.com",
            DisplayName = "Test User",
            Role = UserRole.Admin,
            EntraId = "entra-123",
            IsActive = false
        };

        Assert.Equal("test@example.com", user.Email);
        Assert.Equal("Test User", user.DisplayName);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal("entra-123", user.EntraId);
        Assert.False(user.IsActive);
    }
}
