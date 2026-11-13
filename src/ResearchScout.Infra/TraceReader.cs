using Microsoft.EntityFrameworkCore;

namespace ResearchScout.Infra;

/// <summary>Reads saved traces for <c>scout trace</c> and the API.</summary>
public sealed class TraceReader(IDbContextFactory<ScoutDbContext> dbFactory)
{
    /// <summary>A run with its steps in order, or null if not found.</summary>
    public async Task<RunEntity?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Runs.AsNoTracking()
            .Include(r => r.Steps.OrderBy(s => s.Number))
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Newest runs first, without steps.</summary>
    public async Task<IReadOnlyList<RunEntity>> ListRecentAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Runs.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
