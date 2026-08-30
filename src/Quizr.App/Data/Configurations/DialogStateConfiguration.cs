using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class DialogStateConfiguration : IEntityTypeConfiguration<DialogState>
{
    public void Configure(EntityTypeBuilder<DialogState> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedOnAdd();

        builder.Property(d => d.TeamId).HasConversion(IdConverters.Team);
        builder.HasOne<Team>().WithMany().HasForeignKey(d => d.TeamId);

        builder.Property(d => d.PlayerId).HasConversion(IdConverters.Player);
        builder.HasOne<Player>().WithMany().HasForeignKey(d => d.PlayerId);

        builder.Property(d => d.ChatId).HasConversion(IdConverters.TelegramChat);
        builder.HasIndex(d => new { d.ChatId, d.PlayerId }).IsUnique();

        builder.Property(d => d.MessageId).HasConversion(IdConverters.TelegramMessage);

        builder.Property(d => d.Data).HasColumnType("jsonb");
    }
}
