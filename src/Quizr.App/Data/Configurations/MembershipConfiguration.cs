using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.HasKey(m => new { m.TeamId, m.PlayerId });

        builder.Property(m => m.TeamId).HasConversion(IdConverters.Team);
        builder.Property(m => m.PlayerId).HasConversion(IdConverters.Player);

        builder.HasOne<Team>().WithMany().HasForeignKey(m => m.TeamId);
        builder.HasOne<Player>().WithMany().HasForeignKey(m => m.PlayerId);
    }
}
