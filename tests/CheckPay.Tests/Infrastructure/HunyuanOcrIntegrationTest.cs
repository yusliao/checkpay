using CheckPay.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

/// <summary>
/// 混元 OCR 集成测试 —— 需要真实 API 凭证才能跑
/// 平时 CI 跳过，本地手动验证用
/// </summary>
public class HunyuanOcrIntegrationTest
{
    // 用一张公开可访问的美国支票样本图片（Wiki Commons 公开图）
    private const string SampleCheckUrl =
        "https://upload.wikimedia.org/wikipedia/commons/thumb/0/0b/Check_from_the_United_States.jpg/1280px-Check_from_the_United_States.jpg";

    [Fact(Skip = "集成测试，需要真实混元 API 凭证，手动跑")]
    public async Task ProcessCheckImageAsync_ShouldReturnParsedResult_WithRealApi()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var secretId = configuration["Hunyuan:SecretId"];
        if (string.IsNullOrWhiteSpace(secretId))
        {
            // 没配凭证就跳过，别傻乎乎地报错
            return;
        }

        var service = new HunyuanOcrService(configuration, NullLogger<HunyuanOcrService>.Instance);

        // Act
        var result = await service.ProcessCheckImageAsync(SampleCheckUrl);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.CheckNumber);
        Assert.True(result.Amount > 0, $"金额应该大于0，实际: {result.Amount}");
        Assert.True(result.ConfidenceScores.ContainsKey("CheckNumber"));
        Assert.True(result.ConfidenceScores.ContainsKey("Amount"));
        Assert.True(result.ConfidenceScores.ContainsKey("Date"));

        // 打印识别结果，方便肉眼核对
        Console.WriteLine($"支票号: {result.CheckNumber}");
        Console.WriteLine($"金额: {result.Amount}");
        Console.WriteLine($"日期: {result.Date:yyyy-MM-dd}");
        Console.WriteLine($"置信度 - 支票号: {result.ConfidenceScores["CheckNumber"]:P0}");
        Console.WriteLine($"置信度 - 金额: {result.ConfidenceScores["Amount"]:P0}");
        Console.WriteLine($"置信度 - 日期: {result.ConfidenceScores["Date"]:P0}");
    }
}
