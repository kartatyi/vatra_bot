using System.Text.Json;
using LeBot.Application.Ports;
using LeBot.Domain.Common;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.Maintenance;

/// <summary>
/// The file is a contract with tools/watchdog.ps1: these tests pin its place and its field names, since the
/// watchdog restarts the bot on what it reads there.
/// </summary>
public sealed class HeartbeatWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 13, 10, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lebot-heartbeat-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Now);
    private readonly BotHealthSignal _health = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Write_Always_LandsBesideTheJournal()
    {
        var sut = CreateSut();

        sut.Write();

        File.Exists(Path.Combine(_dir, "data", "heartbeat.json")).Should().BeTrue();
    }

    [Fact]
    public void Write_LoopHasTurned_RecordsWhenAndWhetherTelegramAnswered()
    {
        _health.MarkLoopTurned(Now.AddMinutes(-3), polledOk: true);
        _health.MarkLoopTurned(Now.AddMinutes(-1), polledOk: false);

        var beat = WriteAndRead();

        beat.GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);
        beat.GetProperty("version").GetString().Should().Be("1.2.3");
        beat.GetProperty("writtenAt").GetDateTimeOffset().Should().Be(Now);
        beat.GetProperty("loopAt").GetDateTimeOffset().Should().Be(Now.AddMinutes(-1));
        beat.GetProperty("polledOkAt").GetDateTimeOffset().Should().Be(Now.AddMinutes(-3));
    }

    [Fact]
    public void Write_BeforeTheFirstPoll_LeavesTheLoopFieldsNull()
    {
        var beat = WriteAndRead();

        beat.GetProperty("loopAt").ValueKind.Should().Be(JsonValueKind.Null);
        beat.GetProperty("polledOkAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Write_Again_ReplacesThePreviousBeat()
    {
        var sut = CreateSut();
        sut.Write();
        _time.Advance(TimeSpan.FromMinutes(1));

        sut.Write();

        Read().GetProperty("writtenAt").GetDateTimeOffset().Should().Be(Now.AddMinutes(1));
        Directory.GetFiles(Path.Combine(_dir, "data")).Should().ContainSingle("the temp file was renamed over the old beat");
    }

    [Fact]
    public void Write_FolderCannotBeWritten_DoesNotThrow()
    {
        // A file where the folder should be: every write fails, and the bot must carry on regardless.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "data"), "not a folder");

        var write = () => CreateSut().Write();

        write.Should().NotThrow();
    }

    private JsonElement WriteAndRead()
    {
        CreateSut().Write();
        return Read();
    }

    private JsonElement Read() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "data", "heartbeat.json"))).RootElement;

    private HeartbeatWriter CreateSut()
    {
        var version = Substitute.For<IAppVersion>();
        version.Current.Returns(new ReleaseVersion(1, 2, 3));
        var telemetry = Options.Create(new TelemetryOptions { DatabasePath = Path.Combine(_dir, "data", "lebot.db") });
        return new HeartbeatWriter(_health, version, telemetry, _time, NullLogger<HeartbeatWriter>.Instance);
    }
}
