using Quizr.Domain;

namespace Quizr.App.Services;

// NewFranchise walks Name -> Venue -> Capacity -> Price (or skip) -> Schedule, one step at a
// time; Step names which reply is pending. Everything before Step has already validated.
internal sealed record NewFranchiseDialogData(
    string Step,
    string? Name = null,
    string? Venue = null,
    int? Capacity = null,
    decimal? Price = null
)
{
    public const string AskName = "Name";
    public const string AskVenue = "Venue";
    public const string AskCapacity = "Capacity";
    public const string AskPrice = "Price";
    public const string AskSchedule = "Schedule";
}

// EditFranchise: pick-a-field, then one reply applies to just that field.
internal sealed record EditFranchiseDialogData(FranchiseId FranchiseId, int? FieldIndex)
{
    public const int Name = 0;
    public const int Venue = 1;
    public const int Capacity = 2;
    public const int Price = 3;
    public const int Schedule = 4;
}
