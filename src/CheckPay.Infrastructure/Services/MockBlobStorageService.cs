using CheckPay.Application.Common.Interfaces;

namespace CheckPay.Infrastructure.Services;

public class MockBlobStorageService : IBlobStorageService
{
    public Task<string> UploadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        // Mock上传，返回一个假的URL
        var mockUrl = $"https://mock-storage.local/checkpay-files/{Guid.NewGuid()}_{fileName}";
        return Task.FromResult(mockUrl);
    }

    public Task<Stream> DownloadAsync(string blobUrl, CancellationToken cancellationToken = default)
    {
        // Mock下载，返回空流
        return Task.FromResult<Stream>(new MemoryStream());
    }

    public Task DeleteAsync(string blobUrl, CancellationToken cancellationToken = default)
    {
        // Mock删除，什么都不做
        return Task.CompletedTask;
    }
}
