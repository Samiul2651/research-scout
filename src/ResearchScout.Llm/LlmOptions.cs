namespace ResearchScout.Llm;

/// <summary>One model the dashboard may pick, with its price for the cost budget.</summary>
public sealed record ModelOption
{
    /// <summary>One of <see cref="LlmProviders"/>.</summary>
    public required string Provider { get; init; }

    /// <summary>Model id sent to the provider, for example "claude-haiku-5-5".</summary>
    public required string ModelId { get; init; }

    /// <summary>Label shown in the dashboard. Falls back to <see cref="ModelId"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>USD per 1 million input tokens.</summary>
    public required decimal InputUsdPerMillion { get; init; }

    /// <summary>USD per 1 million output tokens.</summary>
    public required decimal OutputUsdPerMillion { get; init; }
}

/// <summary>The provider + model currently in use.</summary>
public sealed record LlmSelection(string Provider, string ModelId)
{
    public override string ToString() => $"{Provider}/{ModelId}";
}

/// <summary>Bound from the "Llm" section of appsettings.json.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Models the dashboard may offer. Only models whose provider has an API key are actually offered.</summary>
    public IList<ModelOption> Models { get; init; } = [];

    /// <summary>Used until an admin picks something else (and when the saved pick is no longer available).</summary>
    public string DefaultProvider { get; set; } = LlmProviders.Anthropic;

    public string DefaultModelId { get; set; } = string.Empty;

    /// <summary>Optional base URL per provider, for proxies or self-hosted gateways. Leave empty for the official URL.</summary>
    public IDictionary<string, string> BaseUrls { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Per-request timeout given to each provider SDK.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
