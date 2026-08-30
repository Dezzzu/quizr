using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Quizr.App.Telegram;
using Quizr.Domain;

namespace Quizr.App.Tests;

public class MessageEditDebouncerTests
{
    private static readonly TimeSpan WindowPastDebounce = TimeSpan.FromSeconds(2);

    [Test]
    public async Task CoalescesRapidEditsToTheSameMessageIntoOneCallWithTheLatestText()
    {
        var timeProvider = new FakeTimeProvider();
        var bot = TelegramBotClientTestHelper.Create();
        var debouncer = new MessageEditDebouncer(bot, timeProvider);
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
        var debouncer = new MessageEditDebouncer(bot, timeProvider);
        var chatId = new TelegramChatId(1);
        var ct = TestContext.Current!.Execution.CancellationToken;

        await debouncer.ScheduleAsync(chatId, new TelegramMessageId(100), "a", null, ct);
        await debouncer.ScheduleAsync(chatId, new TelegramMessageId(200), "b", null, ct);

        timeProvider.Advance(WindowPastDebounce);
        await WaitUntilAsync(() => bot.EditedTexts().Count >= 2, ct);

        bot.EditedTexts().Should().BeEquivalentTo(["a", "b"]);
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
