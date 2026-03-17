using CheckPay.Infrastructure.Services;

namespace CheckPay.Tests.Infrastructure;

public class MockBlobStorageServiceTests
{
    [Fact]
    public async Task UploadAsync_ShouldReturnMockUrl()
    {
        var service = new MockBlobStorageService();
        using var stream = new MemoryStream();
        var fileName = "test.jpg";

        var url = await service.UploadAsync(stream, fileName);

        Assert.NotNull(url);
        Assert.StartsWith("https://mock-storage.local/checkpay-files/", url);
        Assert.Contains(fileName, url);
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnEmptyStream()
    {
        var service = new MockBlobStorageService();
        var blobUrl = "https://mock-storage.local/checkpay-files/test.jpg";

        var stream = await service.DownloadAsync(blobUrl);

        Assert.NotNull(stream);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task DeleteAsync_ShouldCompleteSuccessfully()
    {
        var service = new MockBlobStorageService();
        var blobUrl = "https://mock-storage.local/checkpay-files/test.jpg";

        await service.DeleteAsync(blobUrl);

        // Mock删除不会抛出异常，测试通过即可
        Assert.True(true);
    }
}
