using LeBot.Domain.Common;

namespace LeBot.Domain.Tests.Common;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3+abc")]
    [InlineData("v1.2.3-rc.1+build.7")]
    [InlineData("  1.2.3  ")]
    public void Parse_WellFormedVariants_ReturnsOneTwoThree(string raw)
    {
        var result = ReleaseVersion.Parse(raw);

        result.Should().BeOfType<Result<ReleaseVersion, VersionParseError>.Ok>()
            .Which.Value.Should().Be(new ReleaseVersion(1, 2, 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyError(string? raw)
    {
        var result = ReleaseVersion.Parse(raw);

        result.Should().BeOfType<Result<ReleaseVersion, VersionParseError>.Err>()
            .Which.Error.Should().Be(VersionParseError.Empty);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.x")]
    [InlineData("v")]
    public void Parse_MalformedShape_ReturnsMalformedError(string raw)
    {
        var result = ReleaseVersion.Parse(raw);

        result.Should().BeOfType<Result<ReleaseVersion, VersionParseError>.Err>()
            .Which.Error.Should().Be(VersionParseError.Malformed);
    }

    [Fact]
    public void Parse_EqualValuesAcrossPrefixes_AreEqual()
    {
        var withPrefix = ReleaseVersion.Parse("v1.2.3");
        var withoutPrefix = ReleaseVersion.Parse("1.2.3");

        withPrefix.Should().BeOfType<Result<ReleaseVersion, VersionParseError>.Ok>();
        withoutPrefix.Should().BeOfType<Result<ReleaseVersion, VersionParseError>.Ok>();

        var a = ((Result<ReleaseVersion, VersionParseError>.Ok)withPrefix).Value;
        var b = ((Result<ReleaseVersion, VersionParseError>.Ok)withoutPrefix).Value;
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Theory]
    [InlineData(2, 0, 0, 1, 9, 9)]
    [InlineData(1, 3, 0, 1, 2, 9)]
    [InlineData(1, 2, 4, 1, 2, 3)]
    public void CompareTo_LeftGreater_ReturnsPositive(int lMaj, int lMin, int lPat, int rMaj, int rMin, int rPat)
    {
        var left = new ReleaseVersion(lMaj, lMin, lPat);
        var right = new ReleaseVersion(rMaj, rMin, rPat);

        left.CompareTo(right).Should().BePositive();
        (left > right).Should().BeTrue();
        (left >= right).Should().BeTrue();
        (left < right).Should().BeFalse();
        (left <= right).Should().BeFalse();
    }

    [Fact]
    public void CompareTo_EqualVersions_ReturnsZeroAndBoundaryOperatorsHold()
    {
        var a = new ReleaseVersion(1, 2, 3);
        var b = new ReleaseVersion(1, 2, 3);

        a.CompareTo(b).Should().Be(0);
        (a >= b).Should().BeTrue();
        (a <= b).Should().BeTrue();
        (a > b).Should().BeFalse();
        (a < b).Should().BeFalse();
    }

    [Fact]
    public void CompareTo_Null_ReturnsPositive()
    {
        new ReleaseVersion(0, 0, 1).CompareTo(null).Should().BePositive();
    }

    [Fact]
    public void ToString_FormatsAsDottedTriple()
    {
        new ReleaseVersion(1, 2, 3).ToString().Should().Be("1.2.3");
    }
}
