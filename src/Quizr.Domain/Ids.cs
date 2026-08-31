namespace Quizr.Domain;

// One chat is one team.
public readonly record struct TeamId(long Value);

// Global — shared across teams.
public readonly record struct PlayerId(long Value);

public readonly record struct FranchiseId(long Value);

public readonly record struct GameId(long Value);

public readonly record struct SignupId(long Value);

public readonly record struct ParticipationId(long Value);

public readonly record struct TelegramUserId(long Value);

public readonly record struct TelegramChatId(long Value);

public readonly record struct TelegramMessageId(long Value);

// Who is performing an action, in the two forms an authorization check needs: the domain
// identity every audit row and signup is keyed on, and the Telegram identity `getChatMember`
// takes. Bundled because authorization lives in the application services (STYLE.md) and
// threading both ids through every captain-only method separately was the alternative.
public readonly record struct Actor(PlayerId PlayerId, TelegramUserId TelegramUserId);
