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

        builder.HasOne(m => m.Team).WithMany(t => t.Memberships).HasForeignKey(m => m.TeamId);
        builder.HasOne(m => m.Player).WithMany(p => p.Memberships).HasForeignKey(m => m.PlayerId);
    }
}
