using System.Text;
using AwesomeAssertions;
using Quizr.App.Telegram;
using Quizr.Domain;

namespace Quizr.App.Tests;

public class CallbackDataTests
{
    [Fact]
    public void RoundTripsAFormattedValue()
    {
        var data = CallbackData.Format(CallbackData.Join, new GameId(142));

        var parsed = CallbackData.TryParse(data, out var verb, out var gameId);

        parsed.Should().BeTrue();
        verb.Should().Be(CallbackData.Join);
        gameId.Should().Be(new GameId(142));
    }

    [Theory]
    [InlineData("no-colon")]
    [InlineData("")]
    [InlineData("j:notanumber")]
    [InlineData(":142")]
    public void FailsToParseInvalidData(string data)
    {
        var parsed = CallbackData.TryParse(data, out _, out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void StaysUnderTheSixtyFourByteCallbackDataCap()
    {
        var data = CallbackData.Format(CallbackData.Drop, new GameId(long.MaxValue));

        Encoding.UTF8.GetByteCount(data).Should().BeLessThanOrEqualTo(64);
    }
}
