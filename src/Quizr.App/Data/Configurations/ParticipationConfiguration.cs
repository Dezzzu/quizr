using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class ParticipationConfiguration : IEntityTypeConfiguration<Participation>
{
    public void Configure(EntityTypeBuilder<Participation> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasConversion(IdConverters.Participation).ValueGeneratedOnAdd();

        builder.Property(p => p.GameId).HasConversion(IdConverters.Game);
        builder.HasIndex(p => p.GameId);
        builder.HasOne(p => p.Game).WithMany(g => g.Participations).HasForeignKey(p => p.GameId);

        builder.Property(p => p.PlayerId).HasConversion(IdConverters.Player);
        builder.HasOne(p => p.Player).WithMany().HasForeignKey(p => p.PlayerId);
    }
}
