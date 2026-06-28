using LeBot.Application;
using LeBot.Host.Installer;
using LeBot.Infrastructure;
using Serilog;

// One-shot installer dispatch: any --install / --uninstall arg short-circuits the host build,
// runs the installer (which self-elevates and either registers or removes the Scheduled Task),
// and exits. Anything else (no args, normal launch under Task Scheduler) falls through to the
// regular Host build.
if (args.Length > 0 && (args[0].Equals("--install", StringComparison.OrdinalIgnoreCase)
    || args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
{
    return Installer.Run(args);
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
