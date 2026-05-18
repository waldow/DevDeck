using DevDeck.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DevDeck.Web.Data;

public sealed class DevDeckDbContext : DbContext
{
    public DevDeckDbContext(DbContextOptions<DevDeckDbContext> options)
        : base(options)
    {
    }

    public DbSet<DevService> DevServices => Set<DevService>();
    public DbSet<ServiceEnvironmentVariable> ServiceEnvironmentVariables => Set<ServiceEnvironmentVariable>();
    public DbSet<ServiceHealthCheck> ServiceHealthChecks => Set<ServiceHealthCheck>();
    public DbSet<ServiceRun> ServiceRuns => Set<ServiceRun>();
    public DbSet<LaunchProfile> LaunchProfiles => Set<LaunchProfile>();
    public DbSet<LaunchProfileService> LaunchProfileServices => Set<LaunchProfileService>();
    public DbSet<ProxyRoute> ProxyRoutes => Set<ProxyRoute>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset natively. Store as ticks (long) so
        // ordering/comparison works server-side while still round-tripping correctly.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DevService>(b =>
        {
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.ServiceType).HasMaxLength(64);
        });

        modelBuilder.Entity<ServiceEnvironmentVariable>(b =>
        {
            b.HasIndex(x => new { x.DevServiceId, x.Key }).IsUnique();
            b.HasOne(x => x.DevService)
                .WithMany(x => x.EnvironmentVariables)
                .HasForeignKey(x => x.DevServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceHealthCheck>(b =>
        {
            b.HasOne(x => x.DevService)
                .WithMany(x => x.HealthChecks)
                .HasForeignKey(x => x.DevServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceRun>(b =>
        {
            b.HasIndex(x => new { x.DevServiceId, x.StartedUtc });
            b.HasOne(x => x.DevService)
                .WithMany(x => x.Runs)
                .HasForeignKey(x => x.DevServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LaunchProfileService>(b =>
        {
            b.HasKey(x => new { x.LaunchProfileId, x.DevServiceId });
            b.HasOne(x => x.LaunchProfile)
                .WithMany(x => x.Services)
                .HasForeignKey(x => x.LaunchProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.DevService)
                .WithMany()
                .HasForeignKey(x => x.DevServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProxyRoute>(b =>
        {
            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => x.MatchPath);
            b.HasOne(x => x.DevService)
                .WithMany(x => x.ProxyRoutes)
                .HasForeignKey(x => x.DevServiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppSetting>()
            .HasIndex(x => x.Key)
            .IsUnique();
    }
}
