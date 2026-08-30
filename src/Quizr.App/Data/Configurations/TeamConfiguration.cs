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
        builder.HasIndex(t => t.ChatId).IsUnique();

        builder.Property(t => t.BoardMessageId).HasConversion(IdConverters.TelegramMessage);
    }
}
