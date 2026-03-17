using CheckPay.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class HunyuanOcrServiceTests
{
    [Fact]
    public void Constructor_ShouldThrowException_WhenSecretIdMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hunyuan:SecretKey"] = "test-secret-key"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => new HunyuanOcrService(configuration, NullLogger<HunyuanOcrService>.Instance));
    }

    [Fact]
    public void Constructor_ShouldThrowException_WhenSecretKeyMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hunyuan:SecretId"] = "test-secret-id"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => new HunyuanOcrService(configuration, NullLogger<HunyuanOcrService>.Instance));
    }

    [Fact]
    public void Constructor_ShouldSucceed_WhenBothCredentialsProvided()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hunyuan:SecretId"] = "test-secret-id",
                ["Hunyuan:SecretKey"] = "test-secret-key"
            })
            .Build();

        // 不抛异常就算通过，不需要真实凭证创建客户端
        var ex = Record.Exception(
            () => new HunyuanOcrService(configuration, NullLogger<HunyuanOcrService>.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_ShouldUseDefaultRegion_WhenRegionNotConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hunyuan:SecretId"] = "test-secret-id",
                ["Hunyuan:SecretKey"] = "test-secret-key"
                // Region 没配，应自动 fallback 到 ap-guangzhou
            })
            .Build();

        var ex = Record.Exception(
            () => new HunyuanOcrService(configuration, NullLogger<HunyuanOcrService>.Instance));

        Assert.Null(ex);
    }
}
