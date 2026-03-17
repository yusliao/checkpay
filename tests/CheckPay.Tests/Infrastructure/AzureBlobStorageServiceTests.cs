using CheckPay.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace CheckPay.Tests.Infrastructure;

public class AzureBlobStorageServiceTests
{
    [Fact]
    public void Constructor_ShouldThrowException_WhenConnectionStringMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:BlobStorage:ContainerName"] = "test-container"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new AzureBlobStorageService(configuration));
    }

    [Fact]
    public void Constructor_ShouldThrowException_WhenContainerNameMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:BlobStorage:ConnectionString"] = "UseDevelopmentStorage=true"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new AzureBlobStorageService(configuration));
    }
}
