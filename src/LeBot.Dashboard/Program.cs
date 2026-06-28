using LeBot.Application.Telemetry;
using LeBot.Dashboard;
using LeBot.Infrastructure;

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
