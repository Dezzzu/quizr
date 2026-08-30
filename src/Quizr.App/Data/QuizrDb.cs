using Microsoft.EntityFrameworkCore;
using Quizr.Domain.Entities;

namespace Quizr.App.Data;

public sealed class QuizrDb : DbContext
{
    public QuizrDb(DbContextOptions<QuizrDb> options)
        : base(options) { }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Franchise> Franchises => Set<Franchise>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Signup> Signups => Set<Signup>();
    public DbSet<Participation> Participations => Set<Participation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<DialogState> DialogStates => Set<DialogState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(QuizrDb).Assembly);

        // Nothing in this app ever deletes a row (CLAUDE.md invariant 7), so a
        // cascade should never have the chance to fire. Restrict everywhere
        // rather than trust each configuration to opt out of EF's default.
        foreach (var foreignKey in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }
}
