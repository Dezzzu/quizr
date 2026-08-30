using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.TeamId).HasConversion(IdConverters.Team);
        builder.HasIndex(a => a.TeamId);
        builder.HasOne<Team>().WithMany().HasForeignKey(a => a.TeamId);

        builder.Property(a => a.GameId).HasConversion(IdConverters.Game);
        builder.HasOne<Game>().WithMany().HasForeignKey(a => a.GameId);

        builder.Property(a => a.ActorPlayerId).HasConversion(IdConverters.Player);
        builder.HasOne<Player>().WithMany().HasForeignKey(a => a.ActorPlayerId);

        builder.Property(a => a.Payload).HasColumnType("jsonb");
    }
}
