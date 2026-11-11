namespace ResearchScout.Llm;

/// <summary>Reads and changes the current provider/model. The admin endpoints (T16) call this.</summary>
public sealed class LlmSelector(ModelCatalog catalog, ILlmSettingsStore store)
{
    /// <summary>
    /// The saved pick if it is still available, otherwise the catalog default.
    /// (A saved pick stops being available if its API key is removed or the model is taken out of config.)
    /// </summary>
    /// <exception cref="InvalidOperationException">No provider has an API key.</exception>
    public async Task<LlmSelection> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var saved = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null && catalog.IsAvailable(saved))
        {
            return saved;
        }

        return catalog.Default
            ?? throw new InvalidOperationException("No LLM is available. Set at least one provider API key, for example ANTHROPIC_API_KEY.");
    }

    /// <summary>Saves a new pick. Returns false (and saves nothing) if the pick is not in the available list.</summary>
    public async Task<bool> TrySetAsync(LlmSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (!catalog.IsAvailable(selection))
        {
            return false;
        }

        await store.SetAsync(selection, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
