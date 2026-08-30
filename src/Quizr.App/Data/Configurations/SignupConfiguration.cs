using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Quizr.Domain.Entities;

namespace Quizr.App.Data.Configurations;

internal sealed class SignupConfiguration : IEntityTypeConfiguration<Signup>
{
    public void Configure(EntityTypeBuilder<Signup> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasConversion(IdConverters.Signup).ValueGeneratedOnAdd();

        builder.Property(s => s.GameId).HasConversion(IdConverters.Game);
        builder.HasIndex(s => new { s.GameId, s.CreatedAt });
        builder.HasOne<Game>().WithMany().HasForeignKey(s => s.GameId);

        builder.Property(s => s.PlayerId).HasConversion(IdConverters.Player);
        builder.HasOne<Player>().WithMany().HasForeignKey(s => s.PlayerId);

        builder.Property(s => s.InvitedByPlayerId).HasConversion(IdConverters.Player);
        builder.HasOne<Player>().WithMany().HasForeignKey(s => s.InvitedByPlayerId);

        builder.Property(s => s.CancelledByPlayerId).HasConversion(IdConverters.Player);
        builder.HasOne<Player>().WithMany().HasForeignKey(s => s.CancelledByPlayerId);
    }
}
