using Quizr.Domain;

namespace Quizr.App.Services;

// The DialogState.Kind this flow uses, and the shape of its Data column. Naming a guest is
// the first consumer of the general-purpose dialog mechanism STACK.md earmarks for
// multi-step flows like game creation — one active dialog per (chat, player), in Postgres
// so a reply after a restart still resolves it.
internal static class DialogKinds
{
    public const string NameGuest = "NameGuest";
}

internal sealed record GuestNameDialogData(SignupId SignupId);
