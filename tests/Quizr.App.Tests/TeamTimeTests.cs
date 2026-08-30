using AwesomeAssertions;
using Quizr.App.Time;

namespace Quizr.App.Tests;

public class TeamTimeTests
{
    [Test]
    public void ConvertsAWinterInstantToBerlinStandardTime()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);

        var local = TeamTime.ConvertToLocal(instant, "Europe/Berlin");

        local.Offset.Should().Be(TimeSpan.FromHours(1));
        local.Hour.Should().Be(19);
    }

    [Test]
    public void ConvertsASummerInstantToBerlinDaylightTime()
    {
        var instant = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);

        var local = TeamTime.ConvertToLocal(instant, "Europe/Berlin");

        local.Offset.Should().Be(TimeSpan.FromHours(2));
        local.Hour.Should().Be(20);
    }

    [Test]
    public void GetUtcOffsetMatchesTheConvertedInstantsOffset()
    {
        var instant = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);

        TeamTime.GetUtcOffset(instant, "Europe/Berlin").Should().Be(TimeSpan.FromHours(2));
    }

    [Test]
    public void SameInstantDifferentZonesProduceDifferentLocalHours()
    {
        var instant = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var berlin = TeamTime.ConvertToLocal(instant, "Europe/Berlin");
        var newYork = TeamTime.ConvertToLocal(instant, "America/New_York");

        berlin.Hour.Should().NotBe(newYork.Hour);
        berlin.ToUniversalTime().Should().Be(newYork.ToUniversalTime());
    }

    [Test]
    public void ConvertsALocalWinterDateAndTimeToTheMatchingUtcInstant()
    {
        var instant = TeamTime.ConvertToUtc(new DateOnly(2026, 1, 15), new TimeOnly(19, 0), "Europe/Berlin");

        instant.Should().Be(new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void ConvertsALocalSummerDateAndTimeToTheMatchingUtcInstant()
    {
        var instant = TeamTime.ConvertToUtc(new DateOnly(2026, 7, 15), new TimeOnly(20, 0), "Europe/Berlin");

        instant.Should().Be(new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void ConvertToUtcRoundTripsWithConvertToLocal()
    {
        var original = new DateTimeOffset(2026, 3, 6, 19, 5, 0, TimeSpan.Zero);
        var local = TeamTime.ConvertToLocal(original, "Europe/Berlin");

        var roundTripped = TeamTime.ConvertToUtc(
            DateOnly.FromDateTime(local.Date),
            TimeOnly.FromDateTime(local.DateTime),
            "Europe/Berlin"
        );

        roundTripped.Should().Be(original);
    }
}
