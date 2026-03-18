using CheckPay.Application.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CheckPay.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ImageProxyController : ControllerBase
{
    private readonly IBlobStorageService _blobStorageService;

    public ImageProxyController(IBlobStorageService blobStorageService)
    {
        _blobStorageService = blobStorageService;
    }

    [HttpGet]
    public async Task<IActionResult> GetImage([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("图片URL不能为空");

        try
        {
            var stream = await _blobStorageService.DownloadAsync(url);
            return File(stream, "image/jpeg");
        }
        catch (Exception ex)
        {
            return NotFound($"图片加载失败: {ex.Message}");
        }
    }
}
