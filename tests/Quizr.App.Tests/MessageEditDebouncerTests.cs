using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quizr.App.Telegram;
using Quizr.Domain;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Quizr.App.Tests;

public class MessageEditDebouncerTests
{
    private static readonly TimeSpan WindowPastDebounce = TimeSpan.FromSeconds(2);

    [Test]
    public async Task CoalescesRapidEditsToTheSameMessageIntoOneCallWithTheLatestText()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        var debouncer = new MessageEditDebouncer(bot, timeProvider, NullLogger<MessageEditDebouncer>.Instance);
        var chatId = new TelegramChatId(1);
        var messageId = new TelegramMessageId(100);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, messageId, "first", null, ct);
        await debouncer.ScheduleAsync(chatId, messageId, "second", null, ct);
        await debouncer.ScheduleAsync(chatId, messageId, "third", null, ct);

        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count > 0, ct);

        bot.EditedTexts().Should().Equal("third");
    }

    [Test]
    public async Task DoesNotCoalesceEditsToDifferentMessages()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        var debouncer = new MessageEditDebouncer(bot, timeProvider, NullLogger<MessageEditDebouncer>.Instance);
        var chatId = new TelegramChatId(1);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, new TelegramMessageId(100), "a", null, ct);
        await debouncer.ScheduleAsync(chatId, new TelegramMessageId(200), "b", null, ct);

        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count >= 2, ct);

        bot.EditedTexts().Should().BeEquivalentTo(["a", "b"]);
    }

    // Telegram rejects a no-op edit as a 400 rather than a silent success (BoardService hits
    // this on every scheduler tick that finds nothing changed) — the flush runs detached from
    // any caller, so this failure has to be swallowed here or it vanishes with no trace and,
    // worse, could leave the debouncer's internal state stuck for this message.
    [Test]
    public async Task ANoOpEditDoesNotPreventALaterEditToTheSameMessage()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: message is not modified", 400));
        var debouncer = new MessageEditDebouncer(bot, timeProvider, NullLogger<MessageEditDebouncer>.Instance);
        var chatId = new TelegramChatId(1);
        var messageId = new TelegramMessageId(100);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, messageId, "same as before", null, ct);
        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count >= 1, ct);

        bot.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 1 });
        await debouncer.ScheduleAsync(chatId, messageId, "a real change", null, ct);
        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Contains("a real change"), ct);

        bot.EditedTexts().Should().Contain("a real change");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
    }
}
