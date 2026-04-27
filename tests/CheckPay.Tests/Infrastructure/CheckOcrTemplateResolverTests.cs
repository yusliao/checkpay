using CheckPay.Domain.Entities;
using CheckPay.Infrastructure.Data;
using CheckPay.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckPay.Tests.Infrastructure;

public class CheckOcrTemplateResolverTests
{
    [Fact]
    public async Task ResolveAsync_MatchesRoutingPrefix_ReturnsProfile()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var ctx = new ApplicationDbContext(options);
        var tid = Guid.NewGuid();
        ctx.OcrCheckTemplates.Add(new OcrCheckTemplate
        {
            Id = tid,
            Name = "Chase021",
            RoutingPrefix = "021",
            BankNameKeywords = null,
            ParsingProfileJson = """{"amountPriorRegion":{"minNormX":0.9,"minNormY":0,"maxNormX":1,"maxNormY":0.2}}""",
            IsActive = true,
            SortOrder = 10
        });
        await ctx.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CheckOcrTemplateResolver(ctx, cache, NullLogger<CheckOcrTemplateResolver>.Instance);

        var r = await resolver.ResolveAsync("021000021", "hello chase", CancellationToken.None);

        Assert.Equal(tid, r.TemplateId);
        Assert.Equal("Chase021", r.TemplateName);
        Assert.NotNull(r.Profile.AmountPriorRegion);
        Assert.True(r.Profile.AmountPriorRegion!.MinNormX >= 0.89);
    }

    [Fact]
    public async Task ResolveAsync_KeywordMismatch_SkipsTemplate()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var ctx = new ApplicationDbContext(options);
        ctx.OcrCheckTemplates.Add(new OcrCheckTemplate
        {
            Name = "NeedsWells",
            RoutingPrefix = null,
            BankNameKeywords = "wells,fargo",
            IsActive = true,
            SortOrder = 5
        });
        await ctx.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CheckOcrTemplateResolver(ctx, cache, NullLogger<CheckOcrTemplateResolver>.Instance);

        var r = await resolver.ResolveAsync(null, "chase bank only", CancellationToken.None);
        Assert.Null(r.TemplateId);
    }
}
