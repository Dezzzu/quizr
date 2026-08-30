using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedOnAdd();

        builder.Property(n => n.SignupId).HasConversion(IdConverters.Signup);
        builder.HasOne<Signup>().WithMany().HasForeignKey(n => n.SignupId);

        // The dedup mechanism: a duplicate notification becomes a rejected
        // insert instead of a second message. See CLAUDE.md's Conventions.
        builder.HasIndex(n => new { n.SignupId, n.Kind }).IsUnique();
    }
}
