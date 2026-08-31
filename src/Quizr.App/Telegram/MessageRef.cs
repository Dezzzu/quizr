using Quizr.Domain;

namespace Quizr.App.Telegram;

// Where one of the bot's messages lives, now that there are two kinds.
//
// An ordinary message is addressed by (chat, message id). An ephemeral one — visible to a
// single member of the chat, Bot API 10.2 — is addressed by (chat, receiver, ephemeral id)
// and is edited and deleted through an entirely separate set of API methods. The two ids
// cannot share a field: Telegram sets Message.Id to 0 for an ephemeral message and reports
// the real handle on Message.EphemeralMessageId, so anything that stored "the message id"
// would quietly be storing zero.
//
// Id therefore means whichever of the two this is — the ordinary message id, or the ephemeral
// one — and ReceiverUserId is what says which. The two are different numbering spaces, so
// this only holds together because MessageRef is the sole carrier of either and nothing
// outside MessageSender unwraps one.
public readonly record struct MessageRef(TelegramChatId ChatId, TelegramMessageId Id, TelegramUserId? ReceiverUserId)
{
    public bool IsEphemeral => ReceiverUserId is not null;

    public static MessageRef Ordinary(TelegramChatId chatId, TelegramMessageId messageId) =>
        new(chatId, messageId, null);

    public static MessageRef Ephemeral(
        TelegramChatId chatId,
        TelegramMessageId ephemeralMessageId,
        TelegramUserId receiver
    ) => new(chatId, ephemeralMessageId, receiver);
}
