using CheckPay.Application.Common.Interfaces;
using CheckPay.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddInfrastructure(configuration);

var serviceProvider = services.BuildServiceProvider();
var blobService = serviceProvider.GetRequiredService<IBlobStorageService>();

Console.WriteLine("=== MinIO 集成测试 ===\n");

try
{
    // 测试上传
    Console.WriteLine("1. 测试文件上传...");
    var testContent = "Hello MinIO from CheckPay!"u8.ToArray();
    using var uploadStream = new MemoryStream(testContent);
    var url = await blobService.UploadAsync(uploadStream, "test.txt");
    Console.WriteLine($"   ✓ 上传成功: {url}\n");

    // 测试下载
    Console.WriteLine("2. 测试文件下载...");
    var downloadStream = await blobService.DownloadAsync(url);
    using var reader = new StreamReader(downloadStream);
    var content = await reader.ReadToEndAsync();
    Console.WriteLine($"   ✓ 下载成功，内容: {content}\n");

    // 测试删除
    Console.WriteLine("3. 测试文件删除...");
    await blobService.DeleteAsync(url);
    Console.WriteLine("   ✓ 删除成功\n");

    Console.WriteLine("=== ✅ 所有测试通过 ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ 测试失败: {ex.Message}");
    Console.WriteLine($"详细信息: {ex}");
    return 1;
}

return 0;
