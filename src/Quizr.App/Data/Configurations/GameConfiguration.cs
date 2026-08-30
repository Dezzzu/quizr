using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class GameConfiguration : IEntityTypeConfiguration<Game>
{
    public void Configure(EntityTypeBuilder<Game> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasConversion(IdConverters.Game).ValueGeneratedOnAdd();

        builder.Property(g => g.TeamId).HasConversion(IdConverters.Team);
        builder.HasIndex(g => g.TeamId);
        builder.HasOne<Team>().WithMany().HasForeignKey(g => g.TeamId);

        builder.Property(g => g.FranchiseId).HasConversion(IdConverters.Franchise);
        builder.HasOne<Franchise>().WithMany().HasForeignKey(g => g.FranchiseId);

        builder.Property(g => g.AnnouncementMessageId).HasConversion(IdConverters.TelegramMessage);

        builder.Property(g => g.CreatedByPlayerId).HasConversion(IdConverters.Player);
        builder.HasOne<Player>().WithMany().HasForeignKey(g => g.CreatedByPlayerId);
    }
}
