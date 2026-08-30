using AwesomeAssertions;

namespace Quizr.App.Tests;

public class HarnessTests
{
    [Test]
    public void TestHarnessIsWiredUp() => true.Should().BeTrue();
}
