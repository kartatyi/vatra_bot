using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Infrastructure.Tests.Diagnostics;

public class DoctorCheckTests
{
    [Fact]
    public void Factories_SetTheMatchingStatus()
    {
        DoctorCheck.Pass("a", "ok").Status.Should().Be(DoctorStatus.Pass);
        DoctorCheck.Warn("b", "hmm").Status.Should().Be(DoctorStatus.Warn);
        DoctorCheck.Fail("c", "bad").Status.Should().Be(DoctorStatus.Fail);
    }

    [Theory]
    [InlineData(DoctorStatus.Pass, "✓")]
    [InlineData(DoctorStatus.Warn, "⚠")]
    [InlineData(DoctorStatus.Fail, "✗")]
    public void Symbol_MapsEachStatusToItsGlyph(DoctorStatus status, string expected) =>
        new DoctorCheck("name", status, "detail").Symbol.Should().Be(expected);
}
