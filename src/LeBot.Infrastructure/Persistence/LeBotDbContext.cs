using System.Globalization;
using LeBot.Application.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the durable repost journal and the per-chat settings. The journal is append-only
/// in practice: the bot only ever inserts <see cref="RepostEvent"/>s and the dashboard only ever reads
/// rollups, so reads run untracked. Settings are the one table the bot updates in place.
/// The Application's <see cref="RepostEvent"/> record is mapped here (in Infrastructure) via the Fluent
/// API, keeping persistence attributes out of the Application layer.
/// </summary>
public sealed class LeBotDbContext(DbContextOptions<LeBotDbContext> options) : DbContext(options)
{
    public DbSet<RepostEvent> RepostEvents => Set<RepostEvent>();

    internal DbSet<StatsOptOutRow> StatsOptOuts => Set<StatsOptOutRow>();

    internal DbSet<LinkSightingRow> LinkSightings => Set<LinkSightingRow>();

    internal DbSet<PreparedPostRow> PreparedPosts => Set<PreparedPostRow>();

    internal DbSet<ChatSettingsRow> ChatSettings => Set<ChatSettingsRow>();

    internal DbSet<RevealableTextRow> RevealableTexts => Set<RevealableTextRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var repostEvent = modelBuilder.Entity<RepostEvent>();
        repostEvent.ToTable("RepostEvents");

        // RepostEvent is a value-like record with no natural key, so the surrogate primary key lives
        // only in the store as a shadow property — the Application type stays free of a DB identity.
        repostEvent.Property<long>("Id").ValueGeneratedOnAdd();
        repostEvent.HasKey("Id");

        // SQLite has no native DateTimeOffset, and EF Core's *default* mapping can't translate comparisons
        // or ORDER BY on it — yet every dashboard read filters by time window, which would leave the whole
        // read side untranslatable. An explicit string converter fixes that: EF then emits plain TEXT
        // comparisons, and this sortable-by-design format makes lexicographic order equal chronological
        // order, so WHERE / ORDER BY / MIN / MAX all work. The format string deliberately mirrors EF's own
        // default DateTimeOffset format (space separator, trimmed fraction, "+00:00"), so any rows a prior
        // build wrote with the default mapping remain byte-identical and keep sorting correctly alongside
        // new ones. UTC-normalising first guarantees the "+00:00" offset the ordering relies on.
        const string occurredAtFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";
        repostEvent.Property(e => e.OccurredAt)
            .HasConversion(
                value => value.ToUniversalTime().ToString(occurredAtFormat, CultureInfo.InvariantCulture),
                stored => DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .IsRequired();
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

        // The /leaders read: one chat's reposts, grouped by who posted them.
        repostEvent.HasIndex(e => new { e.ChatHash, e.SenderId });

        var optOut = modelBuilder.Entity<StatsOptOutRow>();
        optOut.ToTable("StatsOptOuts");
        optOut.HasKey(o => new { o.ChatHash, o.UserId });

        var chatSettings = modelBuilder.Entity<ChatSettingsRow>();
        chatSettings.ToTable("ChatSettings");
        chatSettings.HasKey(s => s.ChatHash);
        // Text for the same reason as Outcome: a raw SELECT should read "Off", not "0".
        chatSettings.Property(s => s.VideoCaption).HasConversion<string>().IsRequired();
        chatSettings.Property(s => s.PostCaption).HasConversion<string>().IsRequired();
        chatSettings.Property(s => s.UpdatedAt)
            .HasConversion(
                value => value.ToUniversalTime().ToString(occurredAtFormat, CultureInfo.InvariantCulture),
                stored => DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .IsRequired();

        var revealable = modelBuilder.Entity<RevealableTextRow>();
        revealable.ToTable("RevealableTexts");
        revealable.HasKey(r => new { r.ChatHash, r.MessageId });
        revealable.Property(r => r.Text).IsRequired();
        // Same sortable text format as OccurredAt, so the expiry sweep's "<" compares chronologically.
        revealable.Property(r => r.CreatedAt)
            .HasConversion(
                value => value.ToUniversalTime().ToString(occurredAtFormat, CultureInfo.InvariantCulture),
                stored => DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .IsRequired();
        revealable.HasIndex(r => r.CreatedAt);

        var sighting = modelBuilder.Entity<LinkSightingRow>();
        sighting.ToTable("LinkSightings");
        sighting.HasKey(s => s.Id);
        sighting.HasIndex(s => new { s.ChatHash, s.LinkKey });
        sighting.HasIndex(s => new { s.ChatHash, s.SenderId });
        sighting.Property(s => s.SeenAt)
            .HasConversion(
                value => value.ToUniversalTime().ToString(occurredAtFormat, CultureInfo.InvariantCulture),
                stored => DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .IsRequired();

        var prepared = modelBuilder.Entity<PreparedPostRow>();
        prepared.ToTable("PreparedPosts");
        prepared.HasKey(p => p.LinkKey);
        prepared.Property(p => p.Kind).HasConversion<string>();
        prepared.Property(p => p.PreparedAt)
            .HasConversion(
                value => value.ToUniversalTime().ToString(occurredAtFormat, CultureInfo.InvariantCulture),
                stored => DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .IsRequired();
    }
}
