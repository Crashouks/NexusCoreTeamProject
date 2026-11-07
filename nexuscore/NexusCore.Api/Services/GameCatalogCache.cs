using Microsoft.Extensions.Caching.Memory;

namespace NexusCore.Api.Services;

public sealed class GameCatalogCache(IMemoryCache cache)
{
    private const int LifetimeSeconds = 30;
    private long _generation;

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        var cacheKey = $"approved-games:{Volatile.Read(ref _generation)}:{key}";
        var value = await cache.GetOrCreateAsync(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(LifetimeSeconds);
            return factory();
        });

        return value ?? throw new InvalidOperationException("The game catalog cache factory returned null.");
    }

    public void Invalidate() => Interlocked.Increment(ref _generation);
}
