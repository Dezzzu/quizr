using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quizr.Domain;

namespace Quizr.App.Data;

// One converter per id struct, shared across every entity configuration that
// has a column of that type — several entities carry a TeamId, for instance.
// EF Core applies a converter registered for a non-nullable struct to its
// nullable form automatically, so these also cover properties like GameId?.
internal static class IdConverters
{
    public static readonly ValueConverter<TeamId, long> Team = new(id => id.Value, value => new TeamId(value));

    public static readonly ValueConverter<PlayerId, long> Player = new(id => id.Value, value => new PlayerId(value));

    public static readonly ValueConverter<FranchiseId, long> Franchise = new(
        id => id.Value,
        value => new FranchiseId(value)
    );

    public static readonly ValueConverter<GameId, long> Game = new(id => id.Value, value => new GameId(value));

    public static readonly ValueConverter<SignupId, long> Signup = new(id => id.Value, value => new SignupId(value));

    public static readonly ValueConverter<TelegramUserId, long> TelegramUser = new(
        id => id.Value,
        value => new TelegramUserId(value)
    );

    public static readonly ValueConverter<TelegramChatId, long> TelegramChat = new(
        id => id.Value,
        value => new TelegramChatId(value)
    );

    public static readonly ValueConverter<TelegramMessageId, long> TelegramMessage = new(
        id => id.Value,
        value => new TelegramMessageId(value)
    );
}
