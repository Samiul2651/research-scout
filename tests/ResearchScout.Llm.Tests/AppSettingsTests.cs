using Microsoft.Extensions.Configuration;

namespace ResearchScout.Llm.Tests;

/// <summary>Loads the real Api appsettings.json so a typo there fails CI instead of the live site.</summary>
public class AppSettingsTests
{
    private static LlmOptions LoadApiOptions()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResearchScout.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir!.FullName, "src", "ResearchScout.Api", "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        return config.GetSection(LlmOptions.SectionName).Get<LlmOptions>()
            ?? throw new InvalidOperationException("The Llm section is missing from appsettings.json.");
    }

    [Fact]
    public void ApiAppSettings_BindAndBuildACatalog()
    {
        var options = LoadApiOptions();

        var catalog = new ModelCatalog(options, TestHelpers.Keys(LlmProviders.All.ToArray()));

        Assert.NotEmpty(options.Models);
        Assert.All(options.Models, m => Assert.True(m.InputUsdPerMillion > 0 && m.OutputUsdPerMillion > 0, $"{m.ModelId} has no price."));
        Assert.Equal(new LlmSelection(options.DefaultProvider, options.DefaultModelId), catalog.Default);
        Assert.Equal(TimeSpan.FromMinutes(2), options.RequestTimeout);
    }

    [Fact]
    public void ApiAppSettings_HaveAModelForEachMainProvider()
    {
        var providers = LoadApiOptions().Models.Select(m => m.Provider).ToHashSet();

        Assert.Contains(LlmProviders.Anthropic, providers);
        Assert.Contains(LlmProviders.Gemini, providers);
        Assert.Contains(LlmProviders.DeepSeek, providers);
    }
}
