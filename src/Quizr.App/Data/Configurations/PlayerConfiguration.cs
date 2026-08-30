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
    }
}
