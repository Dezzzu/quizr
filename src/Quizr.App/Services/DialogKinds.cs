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
}

internal sealed record GuestNameDialogData(SignupId SignupId);

// A stranger the organisers add to the team on the night (CLAUDE.md's "venue-assigned"),
// recorded from the Manage roster view's Add player button. One field, so one step.
internal sealed record AddVenuePlayerDialogData(GameId GameId);
