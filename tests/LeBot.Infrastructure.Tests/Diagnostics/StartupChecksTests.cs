using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Infrastructure.Tests.Diagnostics;

public class StartupChecksTests
{
    [Fact]
    public void Configuration_WhenSerilogResolved_Passes() =>
        StartupChecks.Configuration(serilogConfigured: true).Status.Should().Be(DoctorStatus.Pass);

    [Fact]
    public void Configuration_WhenNoSink_Fails() =>
        StartupChecks.Configuration(serilogConfigured: false).Status.Should().Be(DoctorStatus.Fail);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Token_WhenMissing_Fails(string? token) =>
        StartupChecks.Token(token).Status.Should().Be(DoctorStatus.Fail);

    [Fact]
    public void Token_WhenPresent_Passes() =>
        StartupChecks.Token("123:abc").Status.Should().Be(DoctorStatus.Pass);

    [Fact]
    public void LogDirectory_WritableDirectory_Passes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lebot-doctor-{Guid.NewGuid():N}");

        try
        {
            var check = StartupChecks.LogDirectory(directory);

            check.Status.Should().Be(DoctorStatus.Pass);
            check.Detail.Should().Be(directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LogDirectory_PathOccupiedByAFile_Fails()
    {
        var file = Path.Combine(Path.GetTempPath(), $"lebot-doctor-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, "x");

        try
        {
            StartupChecks.LogDirectory(file).Status.Should().Be(DoctorStatus.Fail);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Cookies_Disabled_Passes()
    {
        var check = StartupChecks.Cookies(cookiesFromBrowser: null, isLocalSystem: false);

        check.Status.Should().Be(DoctorStatus.Pass);
        check.Detail.Should().Contain("disabled");
    }

    [Fact]
    public void Cookies_EnabledForInteractiveUser_Passes() =>
        StartupChecks.Cookies("firefox", isLocalSystem: false).Status.Should().Be(DoctorStatus.Pass);

    [Fact]
    public void Cookies_EnabledUnderLocalSystem_Warns()
    {
        var check = StartupChecks.Cookies("firefox", isLocalSystem: true);

        check.Status.Should().Be(DoctorStatus.Warn);
        check.Detail.Should().Contain("LocalSystem");
    }
}
