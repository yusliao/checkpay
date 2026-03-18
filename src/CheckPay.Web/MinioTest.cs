using CheckPay.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CheckPay.Web;

public static class MinioTest
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var blobService = services.GetRequiredService<IBlobStorageService>();

        Console.WriteLine("=== MinIO 集成测试 ===");

        // 测试上传
        Console.WriteLine("1. 测试文件上传...");
        var testContent = "Hello MinIO from CheckPay!"u8.ToArray();
        using var uploadStream = new MemoryStream(testContent);
        var url = await blobService.UploadAsync(uploadStream, "test.txt");
        Console.WriteLine($"   ✓ 上传成功: {url}");

        // 测试下载
        Console.WriteLine("2. 测试文件下载...");
        var downloadStream = await blobService.DownloadAsync(url);
        using var reader = new StreamReader(downloadStream);
        var content = await reader.ReadToEndAsync();
        Console.WriteLine($"   ✓ 下载成功: {content}");

        // 测试删除
        Console.WriteLine("3. 测试文件删除...");
        await blobService.DeleteAsync(url);
        Console.WriteLine("   ✓ 删除成功");

        Console.WriteLine("=== 所有测试通过 ===");
    }
}
