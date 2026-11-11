using System.Xml.Linq;
using ResearchScout.Harness;

namespace ResearchScout.Harness.Tests;

/// <summary>
/// Enforces the PLAN.md rule: Harness must not reference Papers, Scout or Infra.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenNames =
    [
        "ResearchScout.Papers",
        "ResearchScout.Scout",
        "ResearchScout.Infra",
        "ResearchScout.Llm",
    ];

    // The harness talks to models only through IChatClient. Provider SDKs belong in ResearchScout.Llm.
    private static readonly string[] AllowedHarnessPackages =
    [
        "Microsoft.Extensions.AI.Abstractions",
    ];

    [Fact]
    public void HarnessAssembly_DoesNotReferenceFeatureAssemblies()
    {
        var referenced = typeof(HarnessAssembly).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain(referenced, name => ForbiddenNames.Contains(name));
    }

    [Fact]
    public void HarnessProject_HasNoProjectReferences()
    {
        // The compiler drops unused references, so also check the .csproj itself.
        var csproj = Path.Combine(FindRepoRoot(), "src", "ResearchScout.Harness", "ResearchScout.Harness.csproj");
        var projectReferences = XDocument.Load(csproj).Descendants("ProjectReference").ToList();

        Assert.Empty(projectReferences);
    }

    [Fact]
    public void HarnessProject_UsesOnlyAllowedPackages()
    {
        var csproj = Path.Combine(FindRepoRoot(), "src", "ResearchScout.Harness", "ResearchScout.Harness.csproj");
        var packages = XDocument.Load(csproj)
            .Descendants("PackageReference")
            .Select(p => (string?)p.Attribute("Include") ?? string.Empty);

        Assert.All(packages, package => Assert.Contains(package, AllowedHarnessPackages));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResearchScout.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find ResearchScout.slnx above the test output folder.");
    }
}
