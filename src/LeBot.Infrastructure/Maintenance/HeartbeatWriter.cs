using System.Text.Json;
using LeBot.Application.Ports;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Writes <c>heartbeat.json</c> beside the journal once a minute: when the poll loop last turned and when
/// Telegram last answered it (<see cref="BotHealthSignal"/>). The watchdog reads it to tell a working bot
/// from a process that exists but has stopped polling — on 2026-11-18 a stuck update kept the bot silent in
/// every chat for 2.5 hours while the process looked perfectly alive.
/// </summary>
/// <remarks>
/// It runs on its own timer, not the poll loop's, so a stuck loop still gets reported: the file keeps being
/// rewritten with a <c>loopAt</c> that no longer moves. A file that stops changing altogether means the
/// whole process is frozen. The write is atomic (temp file, then rename), so a reader never sees half of it.
/// </remarks>
public sealed class HeartbeatWriter(
    BotHealthSignal health,
    IAppVersion version,
    IOptions<TelemetryOptions> telemetry,
    TimeProvider timeProvider,
    ILogger<HeartbeatWriter> logger) : BackgroundService
{
    /// <summary>How often the file is rewritten; the watchdog allows several missed beats before it acts.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    internal const string FileName = "heartbeat.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private bool _warned;

    /// <summary>Where the heartbeat lives: the journal's folder, which every deployment already has.</summary>
    internal string FilePath =>
        Path.Combine(Path.GetDirectoryName(telemetry.Value.ResolvedDatabasePath) ?? AppContext.BaseDirectory, FileName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            Write();
        }
        while (await NextBeatAsync(timer, stoppingToken));
    }

    /// <summary>Writes one beat now. Never throws: a heartbeat that can't be written must not take the bot down.</summary>
    internal void Write()
    {
        var beat = new Heartbeat(
            Pid: Environment.ProcessId,
            Version: version.Current.ToString(),
            WrittenAt: timeProvider.GetUtcNow(),
            LoopAt: health.LoopTurnedAt,
            PolledOkAt: health.PolledOkAt);

        var path = FilePath;
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); // non-null: Combine above always yields a directory part
            File.WriteAllText(temp, JsonSerializer.Serialize(beat, Json));
            File.Move(temp, path, overwrite: true);
            _warned = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Once per spell of failures, not once a minute: the watchdog will notice the stale file anyway.
            if (!_warned)
            {
                logger.LogWarning(ex, "Could not write the heartbeat to {Path}", path);
                _warned = true;
            }
        }
    }

    private static async Task<bool> NextBeatAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The file's shape — the contract with <c>tools/watchdog.ps1</c>. Times are UTC, ISO 8601.</summary>
    internal sealed record Heartbeat(
        int Pid,
        string Version,
        DateTimeOffset WrittenAt,
        DateTimeOffset? LoopAt,
        DateTimeOffset? PolledOkAt);
}
