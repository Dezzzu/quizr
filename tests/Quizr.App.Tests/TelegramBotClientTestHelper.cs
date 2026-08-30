using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Quizr.App.Tests;

// SendMessageRequest, EditMessageTextRequest etc. are extension methods over
// ITelegramBotClient.SendRequest<TResult>(IRequest<TResult>, CancellationToken) — the
// interface's only real member — so that's the one method every test fakes.
internal static class TelegramBotClientTestHelper
{
    public static ITelegramBotClient Create()
    {
        var bot = Substitute.For<ITelegramBotClient>();
        var nextMessageId = 1;

        bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new Message { Id = nextMessageId++ });

        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new Message { Id = nextMessageId++ });

        bot.SendRequest(Arg.Any<GetChatMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatMemberMember
            {
                User = new User { Id = 0, FirstName = "" },
            });

        return bot;
    }

    public static IReadOnlyList<string> SentTexts(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Select(request => request.Text)
            .ToList();

    public static IReadOnlyList<string?> EditedTexts(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<EditMessageTextRequest>()
            .Select(request => request.Text)
            .ToList();
}
