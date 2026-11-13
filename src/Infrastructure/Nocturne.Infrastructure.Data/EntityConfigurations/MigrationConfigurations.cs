using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class MigrationSourceEntityConfiguration : IEntityTypeConfiguration<MigrationSourceEntity>
{
    public void Configure(EntityTypeBuilder<MigrationSourceEntity> entity)
    {
        // Sources dedupe per tenant: the identifier alone must not be unique, or tenant B
        // migrating from the same URL as tenant A would collide with (and read) A's source row.
        entity
            .HasIndex(s => new { s.TenantId, s.SourceIdentifier })
            .HasDatabaseName("ix_migration_sources_tenant_identifier")
            .IsUnique();

        entity
            .HasIndex(s => s.LastMigrationAt)
            .HasDatabaseName("ix_migration_sources_last_migration");

        entity
            .HasIndex(s => s.Mode)
            .HasDatabaseName("ix_migration_sources_mode");

        entity
            .HasIndex(s => s.CreatedAt)
            .HasDatabaseName("ix_migration_sources_created_at")
            .IsDescending();
    }
}

internal sealed class MigrationRunEntityConfiguration : IEntityTypeConfiguration<MigrationRunEntity>
{
    public void Configure(EntityTypeBuilder<MigrationRunEntity> entity)
    {
        entity
            .HasIndex(r => r.SourceId)
            .HasDatabaseName("ix_migration_runs_source_id");

        entity
            .HasIndex(r => r.State)
            .HasDatabaseName("ix_migration_runs_state");

        entity
            .HasIndex(r => r.StartedAt)
            .HasDatabaseName("ix_migration_runs_started_at")
            .IsDescending();

        entity
            .HasIndex(r => new { r.SourceId, r.State })
            .HasDatabaseName("ix_migration_runs_source_state");

        entity
            .HasIndex(r => r.TenantId)
            .HasDatabaseName("ix_migration_runs_tenant");
    }
}
