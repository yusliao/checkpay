using CheckPay.Application.Common.Interfaces;
using CheckPay.Application.Common.Models;
using CheckPay.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class AzureOcrServiceTests
{
    private sealed class PassthroughCorrector : ICheckOcrParsedSampleCorrector
    {
        public Task<OcrResultDto> ApplyIfMatchedAsync(OcrResultDto parsed, CancellationToken cancellationToken = default) =>
            Task.FromResult(parsed);
    }

    private sealed class DefaultTemplateResolver : ICheckOcrTemplateResolver
    {
        public Task<OcrTemplateResolution> ResolveAsync(
            string? routingDigits9,
            string extractedFullText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrTemplateResolution(null, null, CheckOcrParsingProfile.Default));
    }

    private static ICheckOcrParsedSampleCorrector Corrector() => new PassthroughCorrector();

    private static ICheckOcrTemplateResolver TemplateResolver() => new DefaultTemplateResolver();

    [Fact]
    public void Constructor_ShouldThrowException_WhenEndpointMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:DocumentIntelligence:ApiKey"] = "test-key"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new AzureOcrService(configuration, NullLogger<AzureOcrService>.Instance, null!, TemplateResolver(), Corrector()));
    }

    [Fact]
    public void Constructor_ShouldThrowException_WhenApiKeyMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:DocumentIntelligence:Endpoint"] = "https://test.cognitiveservices.azure.com/"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new AzureOcrService(configuration, NullLogger<AzureOcrService>.Instance, null!, TemplateResolver(), Corrector()));
    }

    [Fact]
    public void Constructor_ShouldSucceed_WhenDocumentAnalysisCredentialsAreOverridden()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:DocumentIntelligence:Endpoint"] = "https://vision.cognitiveservices.azure.com/",
                ["Azure:DocumentIntelligence:ApiKey"] = "vision-key",
                ["Azure:DocumentIntelligence:DocumentAnalysisEndpoint"] = "https://doc.cognitiveservices.azure.com/",
                ["Azure:DocumentIntelligence:DocumentAnalysisApiKey"] = "di-key"
            })
            .Build();

        _ = new AzureOcrService(
            configuration,
            NullLogger<AzureOcrService>.Instance,
            null!,
            TemplateResolver(),
            Corrector());
    }

    [Theory]
    [InlineData(0, 0.1, true)]
    [InlineData(0, 0.88, true)]
    [InlineData(100, 0.51, true)]
    [InlineData(100, 0.52, false)]
    [InlineData(100, 0.9, false)]
    [InlineData(0.01, 0.9, false)]
    public void ShouldInvokeDiAmountFallback_MatchesVisionWeakThreshold(decimal amount, double conf, bool expected) =>
        Assert.Equal(expected, AzureOcrService.ShouldInvokeDiAmountFallback(amount, conf));
}
