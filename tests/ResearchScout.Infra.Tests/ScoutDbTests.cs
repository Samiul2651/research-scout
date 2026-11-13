namespace ResearchScout.Infra.Tests;

public class ScoutDbTests
{
    [Fact]
    public void ToConnectionString_ParsesSupabaseStyleUrl()
    {
        var cs = ScoutDb.ToConnectionString("postgresql://postgres.abc:p%40ss@aws-0-eu.pooler.supabase.com:6543/postgres");

        Assert.Contains("Host=aws-0-eu.pooler.supabase.com", cs, StringComparison.Ordinal);
        Assert.Contains("Port=6543", cs, StringComparison.Ordinal);
        Assert.Contains("Username=postgres.abc", cs, StringComparison.Ordinal);
        Assert.Contains("Password=p@ss", cs, StringComparison.Ordinal); // %40 decoded
        Assert.Contains("SSL Mode=Require", cs, StringComparison.Ordinal); // hosted → TLS
    }

    [Fact]
    public void ToConnectionString_LocalhostUrl_NoForcedTls()
    {
        var cs = ScoutDb.ToConnectionString("postgres://scout:x@localhost:5432/research_scout");

        Assert.DoesNotContain("SSL Mode", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void ToConnectionString_KeepsPlainConnectionStrings()
    {
        Assert.Equal(ScoutDb.LocalDevConnectionString, ScoutDb.ToConnectionString(ScoutDb.LocalDevConnectionString));
    }
}
