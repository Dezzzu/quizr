namespace Quizr.Domain;

// A closed hierarchy, not per-operation enums: "not a captain" is the same
// failure across create, edit, finish, decline, mark-attendance and remove-player.
// One error-type-to-message-key mapping, translated once. See STYLE.md.
public abstract record BusinessError
{
    public sealed record NotCaptain : BusinessError;

    public sealed record RegistrationClosed : BusinessError;

    public sealed record AlreadySignedUp : BusinessError;

    public sealed record GameAlreadyFinished : BusinessError;

    // The team hasn't set a timezone yet, so nothing that computes a game's start time can run.
    public sealed record TeamNotConfigured : BusinessError;
}
