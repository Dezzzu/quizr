using Quizr.Domain;

namespace Quizr.App.Services;

// EditGame: pick-a-field, then one reply applies to just that field. Capacity is the one
// field routed through Roster.Split + Promotion.Promoted, since it can move someone from
// reserve to playing (CLAUDE.md invariant 2) — every other field is a plain validated setter.
internal sealed record EditGameDialogData(GameId GameId, int? FieldIndex)
{
    public const int Title = 0;
    public const int Venue = 1;
    public const int Capacity = 2;
    public const int Price = 3;
    public const int Notes = 4;
    public const int StartTime = 5;
    public const int Tags = 6;
}
