using Microsoft.EntityFrameworkCore;
using ResearchScout.Infra;

namespace ResearchScout.Papers.Fetching;

/// <summary>Caches external API responses (as text) so repeat lookups are free and don't hit rate limits.</summary>
public interface IApiCache
{
    Task<string?> GetAsync(string key, TimeSpan maxAge, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}

/// <summary>Cache in the api_cache table.</summary>
public sealed class PostgresApiCache(IDbContextFactory<ScoutDbContext> dbFactory, TimeProvider? clock = null) : IApiCache
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<string?> GetAsync(string key, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var oldest = _clock.GetUtcNow() - maxAge;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ApiCache.Where(c => c.Key == key && c.FetchedAt >= oldest).Select(c => c.Value)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO api_cache (key, value, fetched_at) VALUES ({key}, {value}, {_clock.GetUtcNow()})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, fetched_at = EXCLUDED.fetched_at
            """,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Cache in memory (tests, CLI without a database).</summary>
public sealed class InMemoryApiCache : IApiCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Value, DateTimeOffset At)> _items = new();

    public Task<string?> GetAsync(string key, TimeSpan maxAge, CancellationToken cancellationToken) =>
        Task.FromResult(_items.TryGetValue(key, out var item) && item.At >= DateTimeOffset.UtcNow - maxAge ? item.Value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        _items[key] = (value, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
