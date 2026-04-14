using CheckPay.Domain.Entities;

namespace CheckPay.Tests.Domain;

public class CustomerTests
{
    [Fact]
    public void Customer_ShouldInitializeWithDefaultValues()
    {
        var customer = new Customer();

        Assert.Equal(string.Empty, customer.CustomerCode);
        Assert.Equal(string.Empty, customer.CustomerName);
        Assert.True(customer.IsActive);
        Assert.False(customer.IsAuthorized);
    }

    [Fact]
    public void Customer_ShouldSetProperties()
    {
        var customer = new Customer
        {
            CustomerCode = "C001",
            CustomerName = "Test Customer",
            IsActive = false
        };

        Assert.Equal("C001", customer.CustomerCode);
        Assert.Equal("Test Customer", customer.CustomerName);
        Assert.False(customer.IsActive);
    }
}
