using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Quizr.App.Tests;

// SendMessageRequest, EditMessageTextRequest etc. are extension methods over
// ITelegramBotClient.SendRequest<TResult>(IRequest<TResult>, CancellationToken) — the
// interface's only real member — so that's the one method every test fakes.
internal static class TelegramBotClientTestHelper
{
    // MessageEditDebouncer's own repost-on-deleted-message path (RepostAnnouncementAsync)
    // needs a scope factory to resolve a fresh QuizrDb/AnnouncementService — every test that
    // doesn't specifically exercise that path (only MessageEditDebouncerTests does) just
    // needs *a* working factory, since nothing here ever calls CreateScope(). A real, empty
    // container gives that for free, no NSubstitute wiring required.
    public static IServiceScopeFactory NullScopeFactory() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    public static ITelegramBotClient Create()
    {
        var bot = Substitute.For<ITelegramBotClient>();
        var nextMessageId = 1;

        // Telegram answers an ephemeral send with Id 0 and the real handle on
        // EphemeralMessageId — mimicked exactly, so anything that reads the wrong one is
        // caught here rather than by silently addressing message 0 in production.
        bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                call.Arg<SendMessageRequest>().EphemeralMessageParameters is null
                    ? new Message { Id = nextMessageId++ }
                    : new Message { Id = 0, EphemeralMessageId = nextMessageId++ }
            );

        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new Message { Id = nextMessageId++ });

        bot.SendRequest(Arg.Any<GetChatMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatMemberMember
            {
                User = new User { Id = 0, FirstName = "" },
            });

        return bot;
    }

    // Deliberately excludes ephemeral sends: a test asserting "the chat saw this" should fail
    // the moment a message becomes visible to one person only. EphemeralTexts is the opposite
    // question, asked separately.
    public static IReadOnlyList<string> SentTexts(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Where(request => request.EphemeralMessageParameters is null)
            .Select(request => request.Text)
            .ToList();

    // What was sent privately, and to whom.
    public static IReadOnlyList<(long ReceiverUserId, string Text)> EphemeralTexts(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Where(request => request.EphemeralMessageParameters is not null)
            .Select(request => (request.EphemeralMessageParameters!.ReceiverUserId, request.Text))
            .ToList();

    public static IReadOnlyList<EditEphemeralMessageTextRequest> EphemeralEdits(this ITelegramBotClient bot) =>
        bot.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<EditEphemeralMessageTextRequest>().ToList();

    // Scoped to one chat — the scheduler processes every team in the database each tick, and
    // PostgresFixture shares one database across every test in the class, so a scheduler
    // test's assertions need to ignore sibling tests' teams rather than see every send ever
    // made against the shared fake bot.
    public static IReadOnlyList<string> SentTexts(this ITelegramBotClient bot, long chatId) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Where(request => request.EphemeralMessageParameters is null && request.ChatId.Identifier == chatId)
            .Select(request => request.Text)
            .ToList();

    public static IReadOnlyList<string> EphemeralTexts(this ITelegramBotClient bot, long chatId) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Where(request => request.EphemeralMessageParameters is not null && request.ChatId.Identifier == chatId)
            .Select(request => request.Text)
            .ToList();

    public static IReadOnlyList<string?> EditedTexts(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<EditMessageTextRequest>()
            .Select(request => request.Text)
            .ToList();

    public static IReadOnlyList<string?> EditedTexts(this ITelegramBotClient bot, long chatId) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<EditMessageTextRequest>()
            .Where(request => request.ChatId.Identifier == chatId)
            .Select(request => request.Text)
            .ToList();

    public static IReadOnlyList<string?> AnsweredCallbackAlerts(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<AnswerCallbackQueryRequest>()
            .Where(request => request.ShowAlert)
            .Select(request => request.Text)
            .ToList();

    public static int PinCallCount(this ITelegramBotClient bot) =>
        bot.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<PinChatMessageRequest>().Count();

    // The real keyboard the bot most recently sent to a chat — for asserting against a
    // button's actual callback data rather than one a test reconstructs independently, which
    // would pass even if the render itself encoded the wrong id.
    public static InlineKeyboardMarkup? LastSentKeyboard(this ITelegramBotClient bot, long chatId) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<SendMessageRequest>()
            .Where(request => request.ChatId.Identifier == chatId)
            .Select(request => request.ReplyMarkup as InlineKeyboardMarkup)
            .LastOrDefault();

    // Counts both kinds: a private prompt is stripped through editEphemeralMessageReplyMarkup
    // rather than the ordinary method, and every caller here is asking "was the keyboard taken
    // away", not which API family did it.
    public static IReadOnlyList<int> DeletedMessageIds(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<DeleteMessageRequest>()
            .Select(request => request.MessageId)
            .ToList();

    public static int ClearedKeyboardCount(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .Count(request =>
                request
                    is EditMessageReplyMarkupRequest { ReplyMarkup: null }
                        or EditEphemeralMessageReplyMarkupRequest { ReplyMarkup: null }
            );

    public static IReadOnlyList<EditMessageReplyMarkupRequest> ClearedKeyboards(this ITelegramBotClient bot) =>
        bot.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<EditMessageReplyMarkupRequest>()
            .Where(request => request.ReplyMarkup == null)
            .ToList();
}
