using AwesomeAssertions;
using Quizr.App.Calendar;
using Quizr.Domain;

namespace Quizr.App.Tests;

// Four things go into the key, and each one is here because leaving it out produces a client
// that stops seeing changes. These tests are that list, one per part.
public class CalendarCacheKeyTests
{
    private static readonly PlayerId Player = new(7);
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Test]
    public void TheSameInputsGiveTheSameKey()
    {
        CalendarCacheKey.Compute(Player, 3, Today).Should().Be(CalendarCacheKey.Compute(Player, 3, Today));
    }

    [Test]
    public void ADifferentPlayerGetsADifferentKey()
    {
        CalendarCacheKey.Compute(Player, 3, Today).Should().NotBe(CalendarCacheKey.Compute(new PlayerId(8), 3, Today));
    }

    // The version is what CalendarVersionInterceptor moves on every change to somebody's feed.
    [Test]
    public void ABumpedVersionGetsADifferentKey()
    {
        CalendarCacheKey.Compute(Player, 3, Today).Should().NotBe(CalendarCacheKey.Compute(Player, 4, Today));
    }

    // The rolling window means what belongs in a feed changes as days pass with no data change
    // at all. Without the date, a game crossing the twelve-month horizon would never appear to
    // a client that keeps sending the same If-None-Match.
    [Test]
    public void ANewDayGetsADifferentKey()
    {
        CalendarCacheKey
            .Compute(Player, 3, Today)
            .Should()
            .NotBe(CalendarCacheKey.Compute(Player, 3, Today.AddDays(1)));
    }

    // The fourth part can't be varied from a test, since it is a compile-time constant — so
    // this pins that it is actually in the key. Bumping CalendarFormat.Version is what
    // invalidates every client's cached body after a deploy that changes the .ics output; if it
    // ever stopped being an input, fixing a serializer bug would keep answering 304 against the
    // broken version forever.
    [Test]
    public void TheFormatVersionIsPartOfTheKey()
    {
        var key = CalendarCacheKey.Compute(Player, 3, Today);

        key.Should().NotBe(Recompute(CalendarFormat.Version + 1));
    }

    // An internal row id and a change count should not travel through proxies and client logs
    // on every request, which is why the ETag is the hash and not the parts.
    [Test]
    public void TheKeyRevealsNeitherThePlayerIdNorTheVersion()
    {
        var key = CalendarCacheKey.Compute(new PlayerId(1234567), 89, Today);

        key.Should().NotContain("1234567").And.NotContain("89").And.NotContain("2026");
    }

    [Test]
    public void AnETagIsTheKeyInQuotes()
    {
        var key = CalendarCacheKey.Compute(Player, 3, Today);

        CalendarCacheKey.ToETag(key).Should().Be($"\"{key}\"");
    }

    // Mirrors Compute's own construction, so a change to the shape of the hashed string that
    // dropped the format version would leave these two disagreeing.
    private static string Recompute(int formatVersion)
    {
        var parts = $"{formatVersion}:{Player.Value}:3:{Today:yyyy-MM-dd}";

        return System.Buffers.Text.Base64Url.EncodeToString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parts))
        );
    }
}
