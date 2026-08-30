using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasConversion(IdConverters.Team).ValueGeneratedOnAdd();

        builder.Property(t => t.ChatId).HasConversion(IdConverters.TelegramChat);

        // Filtered so a retired team's chat id is free to be reused by the team that
        // actually takes over it (TeamChatMigration) — nothing is ever deleted (invariant
        // 7), but a retired team no longer needs to be unique against the one still live.
        builder.HasIndex(t => t.ChatId).IsUnique().HasFilter("\"DeactivatedAt\" IS NULL");

        // A retired team can now share a ChatId with the active team that took it over — the
        // whole reason the index above is filtered. Every "resolve the team for this chat"
        // query in the app wants the live one; a global filter makes that the default instead
        // of a manual "&& DeactivatedAt == null" that's one missed call site away from a
        // SingleAsync throwing "sequence contains more than one element" the moment a chat
        // ever has both. The few places that genuinely need a retired team too (reactivating
        // one on re-add) opt out explicitly with IgnoreQueryFilters(), which is the point —
        // seeing a retired team is the deliberate exception, not the default.
        builder.HasQueryFilter(t => t.DeactivatedAt == null);

        builder.Property(t => t.BoardMessageId).HasConversion(IdConverters.TelegramMessage);
    }
}
