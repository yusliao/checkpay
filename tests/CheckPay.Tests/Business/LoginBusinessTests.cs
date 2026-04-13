namespace CheckPay.Tests.Business;

/// <summary>
/// 测试Login.razor中的账户校验逻辑
/// 由于逻辑直接写在页面@code块里，这里把相同的规则单独抽出来测试
/// </summary>
public class LoginBusinessTests
{
    // 复制Login.razor中的账户校验规则
    private static string? GetRole(string username, string password)
    {
        if (username == "admin" && password == "admin123") return "Admin";
        if (username == "sales" && password == "sales123") return "Sales";
        if (username == "usfinance" && password == "usfinance123") return "USFinance";
        if (username == "cnfinance" && password == "cnfinance123") return "CNFinance";
        return null;
    }

    [Theory]
    [InlineData("admin", "admin123", "Admin")]
    [InlineData("sales", "sales123", "Sales")]
    [InlineData("usfinance", "usfinance123", "USFinance")]
    [InlineData("cnfinance", "cnfinance123", "CNFinance")]
    public void GetRole_ShouldReturnCorrectRole_ForValidCredentials(string username, string password, string expectedRole)
    {
        var role = GetRole(username, password);
        Assert.Equal(expectedRole, role);
    }

    [Theory]
    [InlineData("admin", "wrongpassword")]
    [InlineData("unknown", "admin123")]
    [InlineData("", "")]
    [InlineData("admin", "")]
    [InlineData("", "admin123")]
    public void GetRole_ShouldReturnNull_ForInvalidCredentials(string username, string password)
    {
        var role = GetRole(username, password);
        Assert.Null(role);
    }

    [Fact]
    public void GetRole_ShouldBeCaseSensitive()
    {
        // 用户名和密码大小写敏感
        Assert.Null(GetRole("Admin", "admin123"));
        Assert.Null(GetRole("ADMIN", "admin123"));
        Assert.Null(GetRole("admin", "Admin123"));
    }
}
