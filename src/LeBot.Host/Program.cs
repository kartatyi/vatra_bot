using LeBot.Application;
using LeBot.Infrastructure;
using Microsoft.Extensions.Hosting;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddSerilog((sp, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext());

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

await builder.Build().RunAsync();
