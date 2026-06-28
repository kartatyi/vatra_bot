using LeBot.Application;
using LeBot.Host.Installer;
using LeBot.Host.Updater;
using LeBot.Infrastructure;
using Serilog;

// One-shot verb dispatch: an --install / --uninstall / --apply-update / --rollback arg
// short-circuits the host build, runs the matching one-shot, and exits. --install/--uninstall
// register or remove the Scheduled Task; --apply-update is the detached relaunch helper spawned
// after a self-update swap; --rollback restores the previous binary. Anything else (no args,
// normal launch under Task Scheduler) falls through to the regular Host build.
if (args.Length > 0)
{
    var verb = args[0].ToLowerInvariant();
    switch (verb)
    {
        case "--install":
        case "--uninstall":
            return Installer.Run(args);
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

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddSerilog((sp, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext());

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

await builder.Build().RunAsync();
return 0;
