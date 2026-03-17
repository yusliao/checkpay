using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;

namespace CheckPay.Tests.Domain;

public class OcrResultTests
{
    [Fact]
    public void OcrResult_ShouldInitializeWithDefaultValues()
    {
        var ocrResult = new OcrResult();

        Assert.Equal(string.Empty, ocrResult.ImageUrl);
        Assert.Equal(OcrStatus.Pending, ocrResult.Status);
        Assert.Null(ocrResult.RawResult);
        Assert.Null(ocrResult.ConfidenceScores);
        Assert.Null(ocrResult.ErrorMessage);
    }

    [Fact]
    public void OcrResult_ShouldSetProperties()
    {
        var ocrResult = new OcrResult
        {
            ImageUrl = "https://example.com/image.jpg",
            Status = OcrStatus.Completed,
            ErrorMessage = "Test error"
        };

        Assert.Equal("https://example.com/image.jpg", ocrResult.ImageUrl);
        Assert.Equal(OcrStatus.Completed, ocrResult.Status);
        Assert.Equal("Test error", ocrResult.ErrorMessage);
    }
}
