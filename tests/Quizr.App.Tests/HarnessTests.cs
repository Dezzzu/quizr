using AwesomeAssertions;

namespace Quizr.App.Tests;

public class HarnessTests
{
    [Fact]
    public void TestHarnessIsWiredUp() => true.Should().BeTrue();
}
