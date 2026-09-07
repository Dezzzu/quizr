using System.Buffers.Text;
using System.Security.Cryptography;

namespace Quizr.App.Calendar;

// The credential, and the whole of the authorization story: a calendar client cannot perform
// interactive auth, so whoever holds the URL is the subscriber. That makes two properties
// non-negotiable — it is unguessable, and it never appears in a log.
//
// 256 bits from a CSPRNG, base64url so it survives a URL path untouched. Long enough that
// enumeration is not a threat model worth reasoning about further; the per-IP rate limit is
// there for the noise, not for the arithmetic.
internal static class CalendarToken
{
    // 32 bytes as unpadded base64url. Fixed, so a route constraint can reject a wrong-sized
    // one before any database work happens.
    public const int Length = 43;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    // Checked before the lookup so a scan costs no query. Deliberately not a constant-time
    // comparison against anything: this is a shape test, and the lookup itself is an indexed
    // equality query rather than a byte-by-byte compare.
    public static bool IsWellFormed(string? value) =>
        value is { Length: Length } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
