using System.Globalization;
using LeBot.Application.Telemetry;
using LeBot.Dashboard;
using LeBot.Infrastructure;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// The journal database the bot writes. A relative path rebases onto the dashboard exe's own directory,
// so the documented layout (dashboard co-located with the bot) makes "data/lebot.db" resolve to the
// bot's journal. Override with Dashboard:DatabasePath (config / env Dashboard__DatabasePath / --Dashboard:DatabasePath).
var configuredPath = builder.Configuration["Dashboard:DatabasePath"] ?? "data/lebot.db";
var databasePath = Path.IsPathRooted(configuredPath)
    ? configuredPath
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));

// Loopback only by default: the dashboard is reached over the operator's SSH tunnel, never exposed on a
// public interface (no auth, by design — the tunnel is the perimeter). ASPNETCORE_URLS / --urls can
// still change the port, but keep the host on 127.0.0.1.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5005");
}

// Run unattended, the dashboard's console goes nowhere, so an exception that turned an endpoint into
// a 500 used to leave no trace — a wedged reader could only be restarted, never diagnosed. A rolling
// file beside the exe keeps the stack. Configured in code, not from appsettings.json: next to the bot
// that file holds the bot's own Serilog section, and two processes on one rolling file fight over it.
var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "dashboard-.log");
builder.Services.AddSerilog(config => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        formatProvider: CultureInfo.InvariantCulture,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRepostJournalReader(databasePath);

var app = builder.Build();

app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html; charset=utf-8"));

app.MapGet("/api/data", async (IRepostEventStore store, TimeProvider clock, int? days, CancellationToken cancellationToken) =>
{
    var snapshot = await DashboardSnapshotLoader.LoadAsync(store, clock, databasePath, days ?? 30, cancellationToken);
    return Results.Json(snapshot);
});

app.Logger.LogInformation("LeBot dashboard reading journal {DatabasePath}", databasePath);

await app.RunAsync();
