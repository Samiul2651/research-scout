using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ResearchScout.Harness;

namespace ResearchScout.Infra;

/// <summary>
/// The live demo's daily cap. Spend is summed per UTC day in daily_spend.
/// Recording uses one atomic upsert (<c>spent = spent + cost</c>), so parallel runs never lose an update.
/// </summary>
public sealed class DailySpendGuard(IDbContextFactory<ScoutDbContext> dbFactory, decimal dailyCapUsd, TimeProvider? clock = null)
    : ISpendGuard
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public decimal DailyCapUsd => dailyCapUsd;

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        var spent = await GetTodaySpentAsync(cancellationToken).ConfigureAwait(false);
        return spent >= dailyCapUsd
            ? string.Create(CultureInfo.InvariantCulture, $"Daily demo limit reached (${spent:0.00} of ${dailyCapUsd:0.00}). Please try again tomorrow (UTC).")
            : null;
    }

    public async Task RecordAsync(decimal costUsd, CancellationToken cancellationToken)
    {
        if (costUsd <= 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO daily_spend (day, spent_usd) VALUES ({Today()}, {costUsd})
            ON CONFLICT (day) DO UPDATE SET spent_usd = daily_spend.spent_usd + EXCLUDED.spent_usd
            """,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<decimal> GetTodaySpentAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var today = Today();
        return await db.DailySpend.Where(d => d.Day == today).Select(d => d.SpentUsd)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
}
