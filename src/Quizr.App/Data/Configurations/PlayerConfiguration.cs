using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasConversion(IdConverters.Player).ValueGeneratedOnAdd();

        builder.Property(p => p.TelegramUserId).HasConversion(IdConverters.TelegramUser);
        builder.HasIndex(p => p.TelegramUserId).IsUnique();

        // The one index the calendar feed's hot path uses: a URL arrives, this turns it into
        // a player in a single probe. Unique because a collision would hand one person
        // another's feed; filtered because most players never ask for a token, and only the
        // rows that have one need to be unique against each other — the same reasoning as
        // FranchiseConfiguration's filtered name index.
        builder.HasIndex(p => p.CalendarToken).IsUnique().HasFilter("\"CalendarToken\" IS NOT NULL");
    }
}
