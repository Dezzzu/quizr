using AwesomeAssertions;
using Quizr.App.Calendar;

namespace Quizr.App.Tests;

// The token is the credential, so its two properties are that it is unguessable and that it
// survives a URL path unchanged.
public class CalendarTokenTests
{
    [Test]
    public void AGeneratedTokenIsWellFormed()
    {
        CalendarToken.IsWellFormed(CalendarToken.Generate()).Should().BeTrue();
    }

    // 32 bytes as unpadded base64url. The fixed length is what lets the route constraint reject
    // a wrong-sized token during routing, before a handler or a query is reached.
    [Test]
    public void AGeneratedTokenIs43UrlSafeCharacters()
    {
        var token = CalendarToken.Generate();

        token.Should().HaveLength(43);
        token.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
    }

    [Test]
    public void ThousandsOfTokensAreAllDistinct()
    {
        var tokens = Enumerable.Range(0, 5000).Select(_ => CalendarToken.Generate()).ToList();

        tokens.Distinct().Should().HaveCount(tokens.Count);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("tooshort")]
    // Right length, wrong alphabet: base64 rather than base64url, so it carries + and /, and
    // the / would change the shape of the path it arrived in.
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+/=")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AMalformedTokenIsRejected(string? token)
    {
        CalendarToken.IsWellFormed(token).Should().BeFalse();
    }
}
