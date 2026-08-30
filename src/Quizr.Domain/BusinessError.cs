namespace Quizr.Domain;

// A closed hierarchy, not per-operation enums: "not a captain" is the same
// failure across create, edit, finish, decline, mark-played and remove-player.
// One error-type-to-message-key mapping, translated once. See STYLE.md.
public abstract record BusinessError
{
    public sealed record NotCaptain : BusinessError;

    public sealed record RegistrationClosed : BusinessError;

    public sealed record AlreadySignedUp : BusinessError;

    public sealed record GameAlreadyFinished : BusinessError;

    // The team hasn't set a timezone yet, so nothing that computes a game's start time can run.
    public sealed record TeamNotConfigured : BusinessError;

    // Dropping, naming a guest, or resolving a guest's team-guest choice all require a live
    // signup to act on.
    public sealed record NotSignedUp : BusinessError;

    // Naming a guest or deciding whether they stay is the inviter's call, not any player's.
    public sealed record NotYourGuest : BusinessError;

    // The guest is already cancelled, already has an owner, or was never a named guest
    // awaiting a keep-or-drop decision.
    public sealed record GuestAlreadyResolved : BusinessError;

    // Nudge has a 5-minute cooldown (CLAUDE.md's ~20 messages/minute/group limit) — not
    // deduplication, so a captain can always send another once it's passed.
    public sealed record NudgeOnCooldown : BusinessError;

    // Participation rows only exist once a game is finished (invariant 10) — before that,
    // the roster is derived from signups, and there's nothing here to add a venue-assigned
    // row to.
    public sealed record GameNotFinished : BusinessError;

    // The (TeamId, Name) unique index only covers live franchises (ArchivedAt IS NULL) — an
    // archived franchise's name is free to reuse. This is what a name collision with a live
    // one becomes instead of an unhandled unique-constraint exception.
    public sealed record FranchiseNameTaken : BusinessError;
}
