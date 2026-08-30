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

        // Filtered so an archived franchise's name is free to reuse (nothing is ever deleted
        // — invariant 7 — but an archived name shouldn't block a live one). Only live
        // franchises need to be unique against each other.
        builder.HasIndex(f => new { f.TeamId, f.Name }).IsUnique().HasFilter("\"ArchivedAt\" IS NULL");
        builder.HasOne(f => f.Team).WithMany(t => t.Franchises).HasForeignKey(f => f.TeamId);

        builder
            .Property(f => f.Schedule)
            .HasConversion(ScheduleConversion.Converter, ScheduleConversion.Comparer)
            .HasColumnType("jsonb");
    }
}
