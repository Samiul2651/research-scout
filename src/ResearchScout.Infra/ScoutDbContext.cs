using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ResearchScout.Infra;

public sealed class ScoutDbContext(DbContextOptions<ScoutDbContext> options) : DbContext(options)
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<StepEntity> Steps => Set<StepEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();
    public DbSet<DailySpendEntity> DailySpend => Set<DailySpendEntity>();
    public DbSet<ApiCacheEntity> ApiCache => Set<ApiCacheEntity>();
    public DbSet<PaperEntity> Papers => Set<PaperEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<EmailDraftEntity> EmailDrafts => Set<EmailDraftEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RunEntity>(e =>
        {
            e.ToTable("runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Status).HasMaxLength(32);
            e.Property(r => r.Provider).HasMaxLength(32);
            e.Property(r => r.Model).HasMaxLength(128);
            e.Property(r => r.CostUsd).HasPrecision(12, 6);
            e.HasIndex(r => r.StartedAt);
            e.HasMany(r => r.Steps).WithOne().HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StepEntity>(e =>
        {
            e.ToTable("steps");
            e.HasKey(s => s.Id);
            e.Property(s => s.Provider).HasMaxLength(32);
            e.Property(s => s.Model).HasMaxLength(128);
            e.Property(s => s.CostUsd).HasPrecision(12, 6);
            e.Property(s => s.ToolCallsJson).HasColumnType("jsonb");
            e.HasIndex(s => new { s.RunId, s.Number }).IsUnique();
        });

        modelBuilder.Entity<AppSettingEntity>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<DailySpendEntity>(e =>
        {
            e.ToTable("daily_spend");
            e.HasKey(d => d.Day);
            e.Property(d => d.SpentUsd).HasPrecision(12, 6);
        });

        modelBuilder.Entity<ApiCacheEntity>(e =>
        {
            e.ToTable("api_cache");
            e.HasKey(c => c.Key);
            e.Property(c => c.Key).HasMaxLength(512);
        });

        modelBuilder.Entity<PaperEntity>(e =>
        {
            e.ToTable("papers");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(64);
            e.Property(p => p.Title).HasMaxLength(1000);
        });

        modelBuilder.Entity<ChunkEntity>(e =>
        {
            e.ToTable("chunks");
            e.HasKey(c => c.Id);
            e.Property(c => c.PaperId).HasMaxLength(64);
            e.HasIndex(c => new { c.PaperId, c.Index }).IsUnique();
            e.HasOne<PaperEntity>().WithMany().HasForeignKey(c => c.PaperId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailDraftEntity>(e =>
        {
            e.ToTable("email_drafts");
            e.HasKey(d => d.Id);
            e.Property(d => d.AuthorId).HasMaxLength(64);
            e.Property(d => d.Subject).HasMaxLength(300);
            e.HasIndex(d => d.CreatedAt);
        });
    }
}

/// <summary>Used by <c>dotnet ef</c> to build migrations. Reads DATABASE_URL or falls back to the local dev database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ScoutDbContext>
{
    public ScoutDbContext CreateDbContext(string[] args) =>
        new(ScoutDb.Options(Environment.GetEnvironmentVariable("DATABASE_URL") ?? ScoutDb.LocalDevConnectionString));
}
