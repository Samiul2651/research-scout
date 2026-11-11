namespace ResearchScout.Llm;

/// <summary>Provider names used in config, the database and the dashboard.</summary>
public static class LlmProviders
{
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string DeepSeek = "deepseek";
    public const string OpenAi = "openai";

    public static readonly IReadOnlyList<string> All = [Anthropic, Gemini, DeepSeek, OpenAi];

    /// <summary>Environment variable that holds each provider's API key.</summary>
    public static string ApiKeyEnvVar(string provider) => provider switch
    {
        Anthropic => "ANTHROPIC_API_KEY",
        Gemini => "GEMINI_API_KEY",
        DeepSeek => "DEEPSEEK_API_KEY",
        OpenAi => "OPENAI_API_KEY",
        _ => throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider)),
    };

    /// <summary>Reads every provider's key from environment variables. Providers with no key are left out.</summary>
    public static IReadOnlyDictionary<string, string> ApiKeysFromEnvironment()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in All)
        {
            var key = Environment.GetEnvironmentVariable(ApiKeyEnvVar(provider));
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys[provider] = key;
            }
        }

        return keys;
    }
}
