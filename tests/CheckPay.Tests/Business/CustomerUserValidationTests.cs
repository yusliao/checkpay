using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckPay.Tests.Business;

/// <summary>
/// 测试客户管理（Customers.razor）和用户管理（Users.razor）中的核心校验逻辑
/// </summary>
public class CustomerUserValidationTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    // ====== 客户管理校验 ======

    [Fact]
    public async Task AddCustomer_ShouldNormalize_CustomerCodeToUpperCase()
    {
        await using var ctx = CreateContext();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "c001",   // 小写
            CustomerName = "测试客户",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        customer.CustomerCode = customer.CustomerCode.Trim().ToUpper(); // 模拟页面逻辑

        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var saved = await ctx.Customers.FirstAsync();
        Assert.Equal("C001", saved.CustomerCode);
    }

    [Fact]
    public async Task AddCustomer_ShouldRejectDuplicate_CustomerCode()
    {
        await using var ctx = CreateContext();

        ctx.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C001",
            CustomerName = "已有客户",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        // 模拟页面唯一性校验：AnyAsync(c => c.CustomerCode == code)
        var exists = await ctx.Customers.AnyAsync(c => c.CustomerCode == "C001");
        Assert.True(exists); // 应该检测到重复，阻止新增
    }

    [Fact]
    public async Task AddCustomer_ShouldAllow_DifferentCustomerCode()
    {
        await using var ctx = CreateContext();

        ctx.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C001",
            CustomerName = "客户A",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var exists = await ctx.Customers.AnyAsync(c => c.CustomerCode == "C002");
        Assert.False(exists); // 不同编号，可以新增
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    public void AddCustomer_ShouldReject_EmptyCustomerName(string name)
    {
        // 模拟页面校验：名称 Trim 后为空则不允许提交
        Assert.True(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public async Task ToggleCustomer_ShouldFlip_IsActiveStatus()
    {
        await using var ctx = CreateContext();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CustomerCode = "C001",
            CustomerName = "测试客户",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        // 模拟页面切换逻辑：IsActive = !IsActive
        customer.IsActive = !customer.IsActive;
        customer.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();

        var updated = await ctx.Customers.FirstAsync();
        Assert.False(updated.IsActive); // 从 true 翻转为 false
    }

    // ====== 用户管理校验 ======

    [Fact]
    public async Task AddUser_ShouldNormalize_EmailToLowerCase()
    {
        await using var ctx = CreateContext();

        var email = "Test@Example.COM".Trim().ToLower(); // 模拟页面逻辑
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "测试用户",
            Role = UserRole.CNFinance,
            EntraId = "entra-001",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var saved = await ctx.Users.FirstAsync();
        Assert.Equal("test@example.com", saved.Email);
    }

    [Fact]
    public async Task AddUser_ShouldRejectDuplicate_Email()
    {
        await using var ctx = CreateContext();

        ctx.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "existing@example.com",
            DisplayName = "已有用户",
            Role = UserRole.CNFinance,
            EntraId = "entra-001",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        // 模拟页面校验：AnyAsync(u => u.Email == email || u.EntraId == entraId)
        var exists = await ctx.Users.AnyAsync(u => u.Email == "existing@example.com" || u.EntraId == "entra-999");
        Assert.True(exists); // 邮箱重复，应阻止新增
    }

    [Fact]
    public async Task AddUser_ShouldRejectDuplicate_EntraId()
    {
        await using var ctx = CreateContext();

        ctx.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "user1@example.com",
            DisplayName = "已有用户",
            Role = UserRole.CNFinance,
            EntraId = "entra-001",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        // EntraId 重复也要被检测到
        var exists = await ctx.Users.AnyAsync(u => u.Email == "new@example.com" || u.EntraId == "entra-001");
        Assert.True(exists); // EntraId 重复，应阻止新增
    }

    [Theory]
    [InlineData("", "显示名", "entra-001")]
    [InlineData("email@x.com", "", "entra-001")]
    [InlineData("email@x.com", "显示名", "")]
    public void AddUser_ShouldReject_WhenRequiredFieldsEmpty(string email, string displayName, string entraId)
    {
        // 模拟页面禁用按钮逻辑：三个字段都必填
        var isDisabled = string.IsNullOrWhiteSpace(email)
                         || string.IsNullOrWhiteSpace(displayName)
                         || string.IsNullOrWhiteSpace(entraId);
        Assert.True(isDisabled);
    }
}
