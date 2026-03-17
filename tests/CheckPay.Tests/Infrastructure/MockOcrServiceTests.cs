using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public class MockOcrServiceTests
{
    [Fact]
    public async Task ProcessCheckImageAsync_ShouldReturnMockResult()
    {
        var service = new MockOcrService();
        var imageUrl = "https://example.com/check.jpg";

        var result = await service.ProcessCheckImageAsync(imageUrl);

        Assert.NotNull(result);
        Assert.StartsWith("MOCK-", result.CheckNumber);
        Assert.True(result.Amount > 0);
        Assert.True(result.Date <= DateTime.Today);
        Assert.NotNull(result.ConfidenceScores);
        Assert.Equal(3, result.ConfidenceScores.Count);
        Assert.True(result.ConfidenceScores["CheckNumber"] > 0.9);
    }
}
