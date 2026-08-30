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

    public const string OneOffTitle = "OneOffTitle";
    public const string OneOffVenue = "OneOffVenue";
    public const string OneOffDate = "OneOffDate";
    public const string OneOffTime = "OneOffTime";
    public const string OneOffCapacity = "OneOffCapacity";
    public const string OneOffPrice = "OneOffPrice";

    public const string Confirm = "Confirm";
    public const string EditingField = "EditingField";

    // Confirm-screen override field indices, shared with the EditingFieldIndex slot above.
    public const int OverrideVenue = 0;
    public const int OverrideCapacity = 1;
    public const int OverridePrice = 2;
    public const int OverrideNotes = 3;
    public const int OverrideTags = 4;
}
