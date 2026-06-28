using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.Diagnostics;

public class StartupConfigLoggerTests
{
    [Fact]
    public async Task StartAsync_AlwaysLogsAnInformationSummary()
    {
        var logger = new CapturingLogger<StartupConfigLogger>();
        var service = Build(new YtDlpOptions { CookiesFromBrowser = "firefox" }, isLocalSystem: false, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Information)
            .Which.Message.Should().Contain("cookies=firefox").And.Contain("environment=Production");
    }

    [Fact]
    public async Task StartAsync_NoCookies_LogsDisabledAndNoWarning()
    {
        var logger = new CapturingLogger<StartupConfigLogger>();
        var service = Build(new YtDlpOptions(), isLocalSystem: true, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.Message.Contains("cookies=disabled"));
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_CookiesUnderLocalSystem_EscalatesToWarning()
    {
        var logger = new CapturingLogger<StartupConfigLogger>();
        var service = Build(new YtDlpOptions { CookiesFromBrowser = "firefox" }, isLocalSystem: true, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("LocalSystem");
    }

    private static StartupConfigLogger Build(YtDlpOptions options, bool isLocalSystem, ILogger<StartupConfigLogger> logger)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");

        return new StartupConfigLogger(
            Options.Create(options),
            environment,
            new FixedHostAccountInfo(isLocalSystem),
            logger);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
