namespace ResearchScout.Llm;

/// <summary>Where the admin's provider/model pick is saved. Postgres version comes in T6.</summary>
public interface ILlmSettingsStore
{
    /// <summary>The saved pick, or null if nothing was saved yet.</summary>
    Task<LlmSelection?> GetAsync(CancellationToken cancellationToken = default);

    Task SetAsync(LlmSelection selection, CancellationToken cancellationToken = default);
}

/// <summary>Keeps the pick in memory. Fine for tests and local runs; lost on restart.</summary>
public sealed class InMemoryLlmSettingsStore : ILlmSettingsStore
{
    private LlmSelection? _selection;

    public Task<LlmSelection?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _selection));

    public Task SetAsync(LlmSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Volatile.Write(ref _selection, selection);
        return Task.CompletedTask;
    }
}
