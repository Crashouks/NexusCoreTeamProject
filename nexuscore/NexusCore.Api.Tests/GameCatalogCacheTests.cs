using Microsoft.Extensions.Caching.Memory;
using NexusCore.Api.Services;

namespace NexusCore.Api.Tests;

public class GameCatalogCacheTests
{
    private sealed record CatalogPage(int Generation);

    [Fact]
    public async Task CachedPage_IsReusedUntilInvalidated()
    {
        var cache = new GameCatalogCache(new MemoryCache(new MemoryCacheOptions()));
        var loads = 0;

        Task<CatalogPage> LoadPage()
        {
            loads++;
            return Task.FromResult(new CatalogPage(loads));
        }

        Assert.Equal(new CatalogPage(1), await cache.GetOrCreateAsync("page:1:size:20", LoadPage));
        Assert.Equal(new CatalogPage(1), await cache.GetOrCreateAsync("page:1:size:20", LoadPage));
        Assert.Equal(1, loads);

        cache.Invalidate();

        Assert.Equal(new CatalogPage(2), await cache.GetOrCreateAsync("page:1:size:20", LoadPage));
        Assert.Equal(2, loads);
    }
}
