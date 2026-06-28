using LeBot.Application;
using LeBot.Host.Diagnostics;
using LeBot.Host.Installer;
using LeBot.Host.Updater;
using LeBot.Infrastructure;
using LeBot.Infrastructure.Configuration;
using Serilog;

// One-shot verb dispatch: an --install / --uninstall / --doctor / --apply-update / --rollback arg
// short-circuits the host build, runs the matching one-shot, and exits. --install/--uninstall
// register or remove the Scheduled Task; --doctor runs a read-only health checklist; --apply-update
// is the detached relaunch helper spawned after a self-update swap; --rollback restores the previous
// binary. Anything else (no args, normal launch under Task Scheduler) falls through to the Host build.
if (args.Length > 0)
{
    var verb = args[0].ToLowerInvariant();
    switch (verb)
    {
        case "--install":
        case "--uninstall":
            return await Installer.Run(args);
        case "--doctor":
            return await Doctor.RunAsync();
        case "--apply-update":
            return ApplyUpdate.Run(args);
        case "--rollback":
            return Rollback.Run(args);
    }
}

// Before building anything, self-heal a freshly-applied update that crash-loops on startup: if this
// binary keeps dying before it can confirm it's serving, restore the previous one and hand off. A
// returned exit code means a rollback was triggered and we must stop so the helper can relaunch.
if (Watchdog.CheckOnStartup() is { } watchdogExitCode)
{
    return watchdogExitCode;
}

// Pin the content root to the executable's own directory, not the launch CWD. Otherwise the host
// resolves appsettings*.json — and the reloadOnChange file watcher — against whatever directory the
// process started in, so a bare-exe launch loads the wrong (or no) config and can even crash when the
// CWD isn't watchable. Task Scheduler masks this by setting a working directory; nothing else does.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Embedded appsettings.json is the base layer, so Serilog/YtDlp/Update defaults exist even when the
// binary was copied somewhere stripped of its JSON files. On-disk appsettings.json (added by the
// builder) and appsettings.Local.json layer on top as optional overrides.
builder.Configuration.AddEmbeddedDefaults();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Pin Serilog's rolling-file sink to an absolute path beside the executable so the logs never scatter
// to whatever working directory the process happened to start in — then announce where they are.
var logPathOverrides = LogPathResolver.ResolveAbsolutePaths(builder.Configuration, AppContext.BaseDirectory);
if (logPathOverrides.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(logPathOverrides);
    foreach (var logPath in logPathOverrides.Values)
    {
        Console.WriteLine($"Logs: {logPath}");
    }
}

builder.Services.AddSerilog((sp, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext());

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

await builder.Build().RunAsync();
return 0;
