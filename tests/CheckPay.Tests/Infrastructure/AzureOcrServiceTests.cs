using CheckPay.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class AzureOcrServiceTests
{
    [Fact]
    public void Constructor_ShouldThrowException_WhenEndpointMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:DocumentIntelligence:ApiKey"] = "test-key"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new AzureOcrService(configuration, NullLogger<AzureOcrService>.Instance, null!));
    }

    [Fact]
    public void Constructor_ShouldThrowException_WhenApiKeyMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:DocumentIntelligence:Endpoint"] = "https://test.cognitiveservices.azure.com/"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new AzureOcrService(configuration, NullLogger<AzureOcrService>.Instance, null!));
    }
}
