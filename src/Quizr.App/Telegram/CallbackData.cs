using Quizr.Domain;

namespace Quizr.App.Telegram;

// Callback data is capped at 64 bytes (CLAUDE.md), so buttons carry a compact
// "verb:id" pair rather than serialized JSON. Game-scoped verbs carry a GameId;
// guest-scoped verbs carry the guest's own SignupId, since a guest's fate isn't
// tied to whoever happens to be tapping the button.
internal static class CallbackData
{
    // Announcement buttons — carry a GameId.
    public const char Join = 'j';
    public const char Guest = 'g';
    public const char Drop = 'd';
    public const char ConfirmDrop = 'D';
    public const char Stay = 'b';
    public const char MyGuests = 'm';
    public const char Nudge = 'v';
    public const char ManageRoster = 'w';

    // Guest-scoped follow-ups — carry a SignupId.
    public const char SkipGuestName = 'N';
    public const char KeepGuest = 'K';
    public const char RemoveGuestToo = 'X';
    public const char RemoveGuest = 'R';

    // Captain flows (franchises, game creation/editing, nudge targeting). Which dialog is
    // active — via the (ChatId, PlayerId)-unique DialogState — decides how each is
    // interpreted, so the same verb is safely reused across flows that never overlap for one
    // person: e.g. EditField's id means something different mid-NewGame vs. mid-EditGame.
    public const char PickFranchise = 'f'; // carries FranchiseId
    public const char ArchiveFranchise = 'z'; // carries FranchiseId
    public const char OneOff = 'o'; // dummy id
    public const char PickDate = 'a'; // carries an index into the dialog's stored candidate dates
    public const char CustomDate = 'J'; // dummy id — a franchise game on a date its schedule doesn't cover
    public const char EditField = 'q'; // carries a field index, meaning scoped by the active dialog
    public const char Confirm = 'c'; // dummy id
    public const char CancelDialog = 'x'; // dummy id
    public const char Skip = 'i'; // dummy id — skips an optional prompt (e.g. price)
    public const char PickGameToEdit = 'u'; // carries GameId
    public const char AddPlayer = 'p'; // carries GameId
    public const char ToggleNudgeTarget = 't'; // carries PlayerId
    public const char SendNudge = 's'; // carries GameId

    // Roster-management toggles — carry a ParticipationId.
    public const char ToggleAttended = 'y';
    public const char TogglePlayed = 'l';

    // Reminder settings (/myreminders) — self-service, carry a slot index or a dummy id.
    public const char CycleReminderChannel = 'e'; // carries a slot index (0/1/2)
    public const char ToggleReserveReminder = 'h'; // dummy id

    // Act on behalf of a player ("Manage players") and captain grant/revoke
    // (/managecaptains) — the same member-list-with-toggle shape used twice more.
    public const char ManagePlayers = 'r'; // carries GameId
    public const char TogglePlayerSignup = 'k'; // carries PlayerId
    public const char ToggleCaptain = 'n'; // carries PlayerId

    // Decline (with confirm, like Drop/ConfirmDrop/Stay) and the no-confirm Finish button —
    // carry a GameId.
    public const char DeclineGame = 'A';
    public const char ConfirmDecline = 'B';
    public const char CancelDecline = 'C';
    public const char FinishGame = 'E';

    // Ends an open-ended view (Manage guests, Manage players) with no further action —
    // dummy id. Shared across every such view rather than one verb per view, since the
    // action is identical: strip the keyboard, clear any dialog behind it.
    public const char CloseView = 'F';

    // Captain-only: manage any guest for a game, including ones a captain isn't signed up
    // for themselves. Mirrors ManagePlayers/managecaptains' member-list-with-toggle shape,
    // one more instance of the same pattern.
    public const char ManageGuests = 'G'; // carries GameId
    public const char AddTeamGuest = 'H'; // carries GameId
    public const char RemoveGuestOnBehalf = 'I'; // carries SignupId

    public static string Format(char verb, GameId gameId) => Format(verb, gameId.Value);

    public static string Format(char verb, SignupId signupId) => Format(verb, signupId.Value);

    public static string Format(char verb, FranchiseId franchiseId) => Format(verb, franchiseId.Value);

    public static string Format(char verb, PlayerId playerId) => Format(verb, playerId.Value);

    public static string Format(char verb, ParticipationId participationId) => Format(verb, participationId.Value);

    // For dummy-id or small-index verbs (Confirm, CancelDialog, Skip, OneOff, PickDate,
    // EditField) that don't carry a domain id at all.
    public static string Format(char verb, long rawValue) => $"{verb}:{rawValue}";

    public static bool TryParse(string data, out char verb, out GameId gameId)
    {
        var ok = TryParse(data, out verb, out long value);
        gameId = ok ? new GameId(value) : default;
        return ok;
    }

    public static bool TryParse(string data, out char verb, out SignupId signupId)
    {
        var ok = TryParse(data, out verb, out long value);
        signupId = ok ? new SignupId(value) : default;
        return ok;
    }

    public static bool TryParse(string data, out char verb, out FranchiseId franchiseId)
    {
        var ok = TryParse(data, out verb, out long value);
        franchiseId = ok ? new FranchiseId(value) : default;
        return ok;
    }

    public static bool TryParse(string data, out char verb, out PlayerId playerId)
    {
        var ok = TryParse(data, out verb, out long value);
        playerId = ok ? new PlayerId(value) : default;
        return ok;
    }

    public static bool TryParse(string data, out char verb, out ParticipationId participationId)
    {
        var ok = TryParse(data, out verb, out long value);
        participationId = ok ? new ParticipationId(value) : default;
        return ok;
    }

    // For dummy-id or small-index verbs — see the Format(char, long) overload above.
    public static bool TryParse(string data, out char verb, out long value)
    {
        var separator = data.IndexOf(':');
        if (separator > 0 && long.TryParse(data.AsSpan(separator + 1), out value))
        {
            verb = data[0];
            return true;
        }

        verb = default;
        value = default;
        return false;
    }

    // Reads just the verb, so a caller can decide which typed TryParse overload
    // applies before committing to one.
    public static bool TryParseVerb(string data, out char verb)
    {
        if (data.Length > 0 && data.IndexOf(':') > 0)
        {
            verb = data[0];
            return true;
        }

        verb = default;
        return false;
    }
}
