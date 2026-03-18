using CheckPay.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace CheckPay.Infrastructure.Services;

public class MinioStorageService : IBlobStorageService
{
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName;
    private readonly string _endpoint;
    private readonly bool _useSSL;

    public MinioStorageService(IConfiguration configuration)
    {
        _endpoint = configuration["Minio:Endpoint"]
            ?? throw new ArgumentNullException(nameof(configuration), "Minio:Endpoint未配置");
        var accessKey = configuration["Minio:AccessKey"]
            ?? throw new ArgumentNullException(nameof(configuration), "Minio:AccessKey未配置");
        var secretKey = configuration["Minio:SecretKey"]
            ?? throw new ArgumentNullException(nameof(configuration), "Minio:SecretKey未配置");
        _bucketName = configuration["Minio:BucketName"]
            ?? throw new ArgumentNullException(nameof(configuration), "Minio:BucketName未配置");

        _useSSL = bool.TryParse(configuration["Minio:UseSSL"], out var ssl) && ssl;

        _minioClient = new MinioClient()
            .WithEndpoint(_endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(_useSSL)
            .Build();
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var bucketExistsArgs = new BucketExistsArgs().WithBucket(_bucketName);
        var bucketExists = await _minioClient.BucketExistsAsync(bucketExistsArgs, cancellationToken);
        if (!bucketExists)
        {
            var makeBucketArgs = new MakeBucketArgs().WithBucket(_bucketName);
            await _minioClient.MakeBucketAsync(makeBucketArgs, cancellationToken);
        }

        var objectName = $"{Guid.NewGuid()}_{fileName}";
        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType(GetContentType(fileName));

        await _minioClient.PutObjectAsync(putObjectArgs, cancellationToken);

        var protocol = _useSSL ? "https" : "http";
        return $"{protocol}://{_endpoint}/{_bucketName}/{objectName}";
    }

    public async Task<Stream> DownloadAsync(string blobUrl, CancellationToken cancellationToken = default)
    {
        var objectName = ExtractObjectNameFromUrl(blobUrl);
        var memoryStream = new MemoryStream();

        var getObjectArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithCallbackStream(stream => stream.CopyTo(memoryStream));

        await _minioClient.GetObjectAsync(getObjectArgs, cancellationToken);
        memoryStream.Position = 0;
        return memoryStream;
    }

    public async Task DeleteAsync(string blobUrl, CancellationToken cancellationToken = default)
    {
        var objectName = ExtractObjectNameFromUrl(blobUrl);
        var removeObjectArgs = new RemoveObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName);

        await _minioClient.RemoveObjectAsync(removeObjectArgs, cancellationToken);
    }

    private string ExtractObjectNameFromUrl(string url)
    {
        var prefix = $"/{_bucketName}/";
        var startIndex = url.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex == -1)
            throw new ArgumentException($"无效的MinIO URL格式: {url}");

        return url.Substring(startIndex + prefix.Length);
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
