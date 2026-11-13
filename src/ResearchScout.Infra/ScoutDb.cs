using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace ResearchScout.Infra;

/// <summary>Creates DbContext options and factories from a connection string or a DATABASE_URL.</summary>
public static class ScoutDb
{
    /// <summary>Matches docker-compose.yml.</summary>
    public const string LocalDevConnectionString =
        "Host=localhost;Port=5432;Database=research_scout;Username=scout;Password=scout_dev_only";

    public static DbContextOptions<ScoutDbContext> Options(string databaseUrlOrConnectionString) =>
        new DbContextOptionsBuilder<ScoutDbContext>()
            .UseNpgsql(ToConnectionString(databaseUrlOrConnectionString), npgsql => npgsql.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention()
            .Options;

    public static IDbContextFactory<ScoutDbContext> CreateFactory(string databaseUrlOrConnectionString) =>
        new PooledDbContextFactory<ScoutDbContext>(Options(databaseUrlOrConnectionString));

    /// <summary>Applies any pending migrations.</summary>
    public static async Task MigrateAsync(IDbContextFactory<ScoutDbContext> factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts either an Npgsql connection string or a URL like Supabase/Render give
    /// (<c>postgresql://user:password@host:5432/db?sslmode=require</c>) and returns an Npgsql connection string.
    /// </summary>
    public static string ToConnectionString(string databaseUrlOrConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUrlOrConnectionString);

        if (!databaseUrlOrConnectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !databaseUrlOrConnectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return databaseUrlOrConnectionString;
        }

        var uri = new Uri(databaseUrlOrConnectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        // Hosted databases need TLS. Keep sslmode from the URL if given, otherwise require it for non-local hosts.
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (Enum.TryParse<SslMode>(query["sslmode"], ignoreCase: true, out var sslMode))
        {
            builder.SslMode = sslMode;
        }
        else if (uri.Host is not ("localhost" or "127.0.0.1"))
        {
            builder.SslMode = SslMode.Require;
        }

        return builder.ConnectionString;
    }
}
