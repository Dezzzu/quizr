using AwesomeAssertions;

namespace Quizr.Domain.Tests;

public class ResultTests
{
    [Fact]
    public void ASuccessCarriesItsValue()
    {
        Result<int> result = 19;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(19);
    }

    [Fact]
    public void AFailureCarriesItsError()
    {
        Result<int> result = new BusinessError.NotCaptain();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<BusinessError.NotCaptain>();
    }

    [Fact]
    public void MatchDispatchesToTheRightBranch()
    {
        Result<int> success = 19;
        Result<int> failure = new BusinessError.RegistrationClosed();

        success.Match(v => v, _ => -1).Should().Be(19);
        failure.Match(v => v, _ => -1).Should().Be(-1);
    }
}
