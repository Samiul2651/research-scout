using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ResearchScout.Infra.Tests;

/// <summary>Runs a test only when TEST_DATABASE_URL points at a Postgres server (CI sets it; locally it is optional).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresFixture.AdminConnectionString))
        {
            Skip = "Set TEST_DATABASE_URL to run Postgres tests.";
        }
    }
}

/// <summary>Creates a fresh, migrated database for one test class and drops it afterwards.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public static string? AdminConnectionString { get; } =
        Environment.GetEnvironmentVariable("TEST_DATABASE_URL") is { Length: > 0 } url ? ScoutDb.ToConnectionString(url) : null;

    private readonly string _databaseName = "scout_test_" + Guid.NewGuid().ToString("N")[..12];

    public IDbContextFactory<ScoutDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (AdminConnectionString is null)
        {
            return;
        }

        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var testDb = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _databaseName }.ConnectionString;
        Factory = ScoutDb.CreateFactory(testDb);
        await ScoutDb.MigrateAsync(Factory);
    }

    public async Task DisposeAsync()
    {
        if (AdminConnectionString is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>A clock tests can move.</summary>
public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
