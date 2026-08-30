using Microsoft.EntityFrameworkCore;
using Quizr.App.Data;
using Quizr.App.Localization;
using Quizr.App.Telegram;
using Quizr.Domain;
using Quizr.Domain.Entities;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Quizr.App.Services;

// Handles my_chat_member: the bot being added to or removed from a group. This is the
// only way a Team comes into existence — see CLAUDE.md's "Team bootstrap".
public sealed class TeamBootstrapService
{
    // PLAN.md's proposed reminder-slot defaults, confirmed for M3. Team settings, so
    // cheap to change per team later.
    private static readonly TimeOnly DefaultEveningBeforeAt = new(20, 0);
    private static readonly TimeOnly DefaultMorningOfAt = new(9, 0);
    private static readonly TimeSpan DefaultBeforeStartLead = TimeSpan.FromHours(2);

    private readonly QuizrDb _db;
    private readonly IMessageSender _sender;
    private readonly IStrings _strings;
    private readonly TimeProvider _clock;

    public TeamBootstrapService(QuizrDb db, IMessageSender sender, IStrings strings, TimeProvider clock)
    {
        _db = db;
        _sender = sender;
        _strings = strings;
        _clock = clock;
    }

    public async Task HandleMyChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)
    {
        var wasIn = IsMember(update.OldChatMember.Status);
        var isIn = IsMember(update.NewChatMember.Status);
        var chatId = new TelegramChatId(update.Chat.Id);

        if (!wasIn && isIn)
        {
            await HandleAddedAsync(update, chatId, ct);
        }
        else if (wasIn && !isIn)
        {
            await HandleRemovedAsync(chatId, ct);
        }
    }

    private async Task HandleAddedAsync(ChatMemberUpdated update, TelegramChatId chatId, CancellationToken ct)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.ChatId == chatId, ct);

        if (team is not null)
        {
            // Nothing is deleted (invariant 7), so a re-add just clears the flag.
            team.DeactivatedAt = null;
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            team = new Team
            {
                ChatId = chatId,
                Name = update.Chat.Title ?? "Quiz team",
                TimeZoneId = null,
                Locale = LocaleResolver.MapToSupported(update.From.LanguageCode) ?? "en",
                EveningBeforeAt = DefaultEveningBeforeAt,
                MorningOfAt = DefaultMorningOfAt,
                BeforeStartLead = DefaultBeforeStartLead,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Teams.Add(team);
            await _db.SaveChangesAsync(ct);
        }

        var strings = _strings.For(team.Locale);
        await _sender.SendAsync(chatId, strings.Text("Setup.Welcome"), null, ct);

        if (update.NewChatMember.Status != ChatMemberStatus.Administrator)
        {
            await _sender.SendAsync(chatId, strings.Text("Setup.NotAdmin"), null, ct);
        }
    }

    private async Task HandleRemovedAsync(TelegramChatId chatId, CancellationToken ct)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.ChatId == chatId, ct);
        if (team is not null)
        {
            team.DeactivatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
    }

    private static bool IsMember(ChatMemberStatus status) =>
        status
            is ChatMemberStatus.Member
                or ChatMemberStatus.Administrator
                or ChatMemberStatus.Creator
                or ChatMemberStatus.Restricted;
}
