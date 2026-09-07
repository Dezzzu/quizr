namespace Quizr.Domain.Entities;

// Global — one row per Telegram user, shared across teams.
public sealed class Player
{
    public PlayerId Id { get; set; }
    public required TelegramUserId TelegramUserId { get; set; }
    public required string DisplayName { get; set; }
    public string? Username { get; set; }

    // Null falls back to the team's locale.
    public string? Locale { get; set; }

    // True once they've started the bot — a bot cannot message anyone who hasn't.
    public bool DmEnabled { get; set; }

    // The credential for this person's calendar feed, and the whole of its authorization:
    // a calendar client cannot perform interactive auth, so whoever holds the URL is the
    // subscriber. Null until they ask for one, and null again once they turn it off.
    // Rotating overwrites it, which reads like a violation of invariant 7 and is not — that
    // invariant protects the audit trail of who did what, and destroying the old credential
    // is the entire operation. See docs/CALENDAR.md.
    public string? CalendarToken { get; set; }

    public DateTimeOffset? CalendarTokenIssuedAt { get; set; }

    // Bumped whenever anything that changes this person's rendered feed changes, by
    // CalendarVersionInterceptor rather than by a call at each service that saves. It exists
    // so a conditional GET can answer 304 from one indexed row read, with no joins and
    // nothing serialized.
    public long CalendarVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // A person may belong to several teams — never a single Membership. Requires
    // .Include(p => p.Memberships), optionally filtered to one team.
    public List<Membership> Memberships { get; set; } = [];
}
