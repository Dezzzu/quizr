using AwesomeAssertions;
using Quizr.App.Time;

namespace Quizr.App.Tests;

public class TeamTimeTests
{
    [Fact]
    public void ConvertsAWinterInstantToBerlinStandardTime()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);

        var local = TeamTime.ConvertToLocal(instant, "Europe/Berlin");

        local.Offset.Should().Be(TimeSpan.FromHours(1));
        local.Hour.Should().Be(19);
    }

    [Fact]
    public void ConvertsASummerInstantToBerlinDaylightTime()
    {
        var instant = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);

        var local = TeamTime.ConvertToLocal(instant, "Europe/Berlin");

        local.Offset.Should().Be(TimeSpan.FromHours(2));
        local.Hour.Should().Be(20);
    }

    [Fact]
    public void GetUtcOffsetMatchesTheConvertedInstantsOffset()
    {
        var instant = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);

        TeamTime.GetUtcOffset(instant, "Europe/Berlin").Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void SameInstantDifferentZonesProduceDifferentLocalHours()
    {
        var instant = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var berlin = TeamTime.ConvertToLocal(instant, "Europe/Berlin");
        var newYork = TeamTime.ConvertToLocal(instant, "America/New_York");

        berlin.Hour.Should().NotBe(newYork.Hour);
        berlin.ToUniversalTime().Should().Be(newYork.ToUniversalTime());
    }
}
