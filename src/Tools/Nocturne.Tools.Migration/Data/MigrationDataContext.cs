using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Tools.Migration.Data;

/// <summary>
/// Lightweight Entity Framework DbContext for data access to main application tables during migration
/// This context is read/write only and does NOT manage schema - schema is owned by NocturneDbContext
/// </summary>
public class MigrationDataContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the MigrationDataContext class
    /// </summary>
    /// <param name="options">The options for this context</param>
    public MigrationDataContext(DbContextOptions<MigrationDataContext> options)
        : base(options) { }

    // Main application entities - for data access only, no schema management
    public DbSet<EntryEntity> Entries { get; set; }
    public DbSet<TreatmentEntity> Treatments { get; set; }
    public DbSet<ProfileEntity> Profiles { get; set; }
    public DbSet<DeviceStatusEntity> DeviceStatuses { get; set; }
    public DbSet<SettingsEntity> Settings { get; set; }
    public DbSet<FoodEntity> Foods { get; set; }
    public DbSet<ActivityEntity> Activities { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure table names to match existing schema created by NocturneDbContext
        // This context does NOT manage schema - it only provides data access
        modelBuilder.Entity<EntryEntity>().ToTable("entries");
        modelBuilder.Entity<TreatmentEntity>().ToTable("treatments");
        modelBuilder.Entity<ProfileEntity>().ToTable("profiles");
        modelBuilder.Entity<DeviceStatusEntity>().ToTable("devicestatus");
        modelBuilder.Entity<SettingsEntity>().ToTable("settings");
        modelBuilder.Entity<FoodEntity>().ToTable("foods");
        modelBuilder.Entity<ActivityEntity>().ToTable("activities");

        // Configure TreatmentEntity owned types (must match NocturneDbContext column mappings)
        TreatmentEntityConfiguration.ConfigureOwnedTypes(modelBuilder);

        // Note: No migrations, no schema creation, no indexes defined here
        // This context assumes the schema already exists and is managed by NocturneDbContext
    }
}
