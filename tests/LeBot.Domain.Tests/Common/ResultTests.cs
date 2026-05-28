using LeBot.Domain.Common;

namespace LeBot.Domain.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Success_IsSuccess_AndCarriesValue()
    {
        var result = Result<int, string>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Should().BeOfType<Result<int, string>.Ok>()
            .Which.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_IsFailure_AndCarriesError()
    {
        var result = Result<int, string>.Failure("boom");

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Should().BeOfType<Result<int, string>.Err>()
            .Which.Error.Should().Be("boom");
    }

    [Fact]
    public void Match_OnSuccess_InvokesOkBranch()
    {
        var result = Result<int, string>.Success(42);

        var outcome = result.Match(
            onOk: v => $"ok:{v}",
            onErr: e => $"err:{e}");

        outcome.Should().Be("ok:42");
    }

    [Fact]
    public void Match_OnFailure_InvokesErrBranch()
    {
        var result = Result<int, string>.Failure("boom");

        var outcome = result.Match(
            onOk: v => $"ok:{v}",
            onErr: e => $"err:{e}");

        outcome.Should().Be("err:boom");
    }
}
