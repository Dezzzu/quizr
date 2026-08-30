namespace Quizr.Domain;

// One chat is one team.
public readonly record struct TeamId(long Value);

// Global — shared across teams.
public readonly record struct PlayerId(long Value);

public readonly record struct FranchiseId(long Value);

public readonly record struct GameId(long Value);

public readonly record struct SignupId(long Value);

public readonly record struct TelegramUserId(long Value);

public readonly record struct TelegramChatId(long Value);

public readonly record struct TelegramMessageId(long Value);
