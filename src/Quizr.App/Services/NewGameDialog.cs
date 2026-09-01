using Quizr.Domain;

namespace Quizr.App.Services;

// NewGame branches at the first step: pick a franchise (then a date from a candidate list
// computed once and stored here, so a slow reply can't pick a drifted date) or go one-off
// (then a sequential Title -> Venue -> Date -> Time -> Capacity -> Price walk). Both paths
// land on the same Confirm step, where Venue/Capacity/Price/Notes/Tags are individually
// overridable — EditingFieldIndex remembers which one a text reply is answering.
internal sealed record NewGameDialogData(
    string Step,
    FranchiseId? FranchiseId = null,
    List<DateOnly>? CandidateDates = null,
    string? Title = null,
    string? Venue = null,
    DateOnly? Date = null,
    TimeOnly? Time = null,
    int? Capacity = null,
    decimal? Price = null,
    string? Notes = null,
    List<string>? Tags = null,
    int? EditingFieldIndex = null
)
{
    public const string ChooseBranch = "ChooseBranch";
    public const string PickDate = "PickDate";

    // A franchise-linked game on a date outside its schedule (or a franchise with no
    // schedule at all — invariant: an absent day is one it doesn't run, and an empty
    // schedule means none of them are fixed) — same date/time text prompts as the one-off
    // walk below, just landing on Confirm instead of continuing into Capacity/Price, since
    // those already came from the franchise's defaults (or wait as overrides on Confirm).
    public const string FranchiseCustomDate = "FranchiseCustomDate";
    public const string FranchiseCustomTime = "FranchiseCustomTime";

    public const string OneOffTitle = "OneOffTitle";
    public const string OneOffVenue = "OneOffVenue";
    public const string OneOffDate = "OneOffDate";
    public const string OneOffTime = "OneOffTime";
    public const string OneOffCapacity = "OneOffCapacity";
    public const string OneOffPrice = "OneOffPrice";

    public const string Confirm = "Confirm";
    public const string EditingField = "EditingField";

    // Confirm-screen override field indices, shared with the EditingFieldIndex slot above.
    // These are persisted inside a dialog's JSON, so a new one is appended rather than
    // slotted in where it reads best — a renumbering would silently repoint any dialog left
    // open across the deploy that did it. The confirm keyboard decides display order.
    public const int OverrideVenue = 0;
    public const int OverrideCapacity = 1;
    public const int OverridePrice = 2;
    public const int OverrideNotes = 3;
    public const int OverrideTags = 4;

    // A franchise game's title is derived ("Kviz, pliz! #12") and a one-off's was typed
    // several steps back; either way the confirm screen is the last chance to correct it
    // before the announcement goes out, so it's editable here like every other field.
    public const int OverrideTitle = 5;
}
