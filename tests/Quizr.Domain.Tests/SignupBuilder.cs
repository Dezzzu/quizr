using Quizr.Domain.Entities;

namespace Quizr.Domain.Tests;

// Twenty-signup scenarios come up constantly here; this keeps each test's
// setup down to the one or two things it's actually about. See STYLE.md.
internal sealed class SignupBuilder
{
    private static long _nextId;

    private readonly GameId _gameId;
    private long? _id;
    private PlayerId? _playerId = new(1);
    private DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _cancelledAt;

    public SignupBuilder(GameId gameId) => _gameId = gameId;

    public SignupBuilder WithId(long id)
    {
        _id = id;
        return this;
    }

    public SignupBuilder At(DateTimeOffset createdAt)
    {
        _createdAt = createdAt;
        return this;
    }

    public SignupBuilder AsGuest()
    {
        _playerId = null;
        return this;
    }

    public SignupBuilder ByPlayer(long playerId)
    {
        _playerId = new PlayerId(playerId);
        return this;
    }

    public SignupBuilder Cancelled(DateTimeOffset cancelledAt)
    {
        _cancelledAt = cancelledAt;
        return this;
    }

    public Signup Build() =>
        new()
        {
            Id = new SignupId(_id ?? ++_nextId),
            GameId = _gameId,
            PlayerId = _playerId,
            CreatedAt = _createdAt,
            CancelledAt = _cancelledAt,
        };

    // A queue of `count` live signups, one minute apart, in registration order.
    public static List<Signup> Queue(GameId gameId, int count, DateTimeOffset? start = null)
    {
        var first = start ?? DateTimeOffset.UtcNow;

        return Enumerable
            .Range(0, count)
            .Select(i => new SignupBuilder(gameId).At(first.AddMinutes(i)).Build())
            .ToList();
    }
}
