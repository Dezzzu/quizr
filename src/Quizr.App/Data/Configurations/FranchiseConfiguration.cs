using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class FranchiseConfiguration : IEntityTypeConfiguration<Franchise>
{
    public void Configure(EntityTypeBuilder<Franchise> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasConversion(IdConverters.Franchise).ValueGeneratedOnAdd();

        builder.Property(f => f.TeamId).HasConversion(IdConverters.Team);
        builder.HasIndex(f => new { f.TeamId, f.Name }).IsUnique();
        builder.HasOne<Team>().WithMany().HasForeignKey(f => f.TeamId);

        builder
            .Property(f => f.Schedule)
            .HasConversion(ScheduleConversion.Converter, ScheduleConversion.Comparer)
            .HasColumnType("jsonb");
    }
}
