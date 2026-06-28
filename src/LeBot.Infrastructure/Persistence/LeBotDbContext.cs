using LeBot.Application.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the durable repost journal. Append-only in practice: the bot only ever inserts
/// <see cref="RepostEvent"/>s and the dashboard only ever reads rollups, so reads run untracked.
/// The Application's <see cref="RepostEvent"/> record is mapped here (in Infrastructure) via the Fluent
/// API, keeping persistence attributes out of the Application layer.
/// </summary>
public sealed class LeBotDbContext(DbContextOptions<LeBotDbContext> options) : DbContext(options)
{
    public DbSet<RepostEvent> RepostEvents => Set<RepostEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var repostEvent = modelBuilder.Entity<RepostEvent>();
        repostEvent.ToTable("RepostEvents");

        // RepostEvent is a value-like record with no natural key, so the surrogate primary key lives
        // only in the store as a shadow property — the Application type stays free of a DB identity.
        repostEvent.Property<long>("Id").ValueGeneratedOnAdd();
        repostEvent.HasKey("Id");

        repostEvent.Property(e => e.OccurredAt).IsRequired();
        repostEvent.Property(e => e.Host).IsRequired();
        repostEvent.Property(e => e.Url).IsRequired();
        // Stored as text, not an int, so a raw `SELECT` over the DB reads "Failure" not "2".
        repostEvent.Property(e => e.Outcome).HasConversion<string>().IsRequired();
        repostEvent.Property(e => e.BotVersion).IsRequired();
        repostEvent.Property(e => e.ChatHash).IsRequired();

        // The dashboard's three hot filters: time window, platform, and outcome.
        repostEvent.HasIndex(e => e.OccurredAt);
        repostEvent.HasIndex(e => e.Host);
        repostEvent.HasIndex(e => e.Outcome);
    }
}
