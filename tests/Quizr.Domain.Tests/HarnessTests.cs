using AwesomeAssertions;

namespace Quizr.Domain.Tests;

public class HarnessTests
{
    [Fact]
    public void TestHarnessIsWiredUp() => true.Should().BeTrue();
}
