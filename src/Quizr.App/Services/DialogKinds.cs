using Quizr.Domain;

namespace Quizr.App.Services;

// The DialogState.Kind this flow uses, and the shape of its Data column. Naming a guest is
// the first consumer of the general-purpose dialog mechanism STACK.md earmarks for
// multi-step flows like game creation — one active dialog per (chat, player), in Postgres
// so a reply after a restart still resolves it.
internal static class DialogKinds
{
    public const string NameGuest = "NameGuest";
    public const string NewFranchise = "NewFranchise";
    public const string EditFranchise = "EditFranchise";
    public const string NewGame = "NewGame";
    public const string EditGame = "EditGame";
    public const string Nudge = "Nudge";
    public const string AddVenuePlayer = "AddVenuePlayer";
    public const string ManagePlayers = "ManagePlayers";
    public const string AddTeamGuest = "AddTeamGuest";
}

internal sealed record GuestNameDialogData(SignupId SignupId);

// A stranger the organisers add to the team on the night (CLAUDE.md's "venue-assigned"),
// recorded from the Manage roster view's Add player button. One field, so one step.
internal sealed record AddVenuePlayerDialogData(GameId GameId);

// Acting on behalf of a player (design decision #2 of M9): remembers which game the member
// list belongs to between taps — mirrors NudgeDialogData. No Step; there's only one screen.
internal sealed record ManagePlayersDialogData(GameId GameId);

// A captain adding a guest who isn't signed up for themselves — the name is collected up
// front rather than anonymous-then-named, since a team guest is always named (invariant 5)
// and there's no owner for an anonymous one to fall back to identifying by.
internal sealed record AddTeamGuestDialogData(GameId GameId);
