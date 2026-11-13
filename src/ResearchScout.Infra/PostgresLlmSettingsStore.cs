using Microsoft.EntityFrameworkCore;
using ResearchScout.Llm;

namespace ResearchScout.Infra;

/// <summary>Saves the admin's provider/model pick in app_settings, so it survives restarts.</summary>
public sealed class PostgresLlmSettingsStore(IDbContextFactory<ScoutDbContext> dbFactory) : ILlmSettingsStore
{
    public const string Key = "llm.selection";

    public async Task<LlmSelection?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var value = await db.AppSettings.Where(s => s.Key == Key).Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Stored as "provider/model".
        var parts = value?.Split('/', 2);
        return parts is { Length: 2 } ? new LlmSelection(parts[0], parts[1]) : null;
    }

    public async Task SetAsync(LlmSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // One statement, so two admins saving at once can't create a duplicate-key error.
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO app_settings (key, value, updated_at) VALUES ({Key}, {selection.ToString()}, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken).ConfigureAwait(false);
    }
}
