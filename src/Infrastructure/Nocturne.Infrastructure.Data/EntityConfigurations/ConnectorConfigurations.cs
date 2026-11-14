using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class ConnectorConfigurationEntityConfiguration : IEntityTypeConfiguration<ConnectorConfigurationEntity>
{
    public void Configure(EntityTypeBuilder<ConnectorConfigurationEntity> entity)
    {
        entity
            .HasIndex(c => new { c.ConnectorName, c.TenantId })
            .HasDatabaseName("ix_connector_configurations_connector_name_tenant")
            .IsUnique();

        // The index above is case-sensitive, so it only means "one row per connector per tenant"
        // while the column holds one spelling of each name. Enforced here rather than trusted to
        // the writers: a writer that predates the rule — an instance still serving during a rolling
        // deploy, or an operator's own SQL — would otherwise insert a row that satisfies the index
        // and that no lookup can ever find again.
        entity
            .ToTable(t => t.HasCheckConstraint(
                "ck_connector_configurations_connector_name_lower",
                "connector_name = lower(connector_name)"));
    }
}

internal sealed class DataSourceMetadataEntityConfiguration : IEntityTypeConfiguration<DataSourceMetadataEntity>
{
    public void Configure(EntityTypeBuilder<DataSourceMetadataEntity> entity)
    {
        entity
            .HasIndex(d => new { d.TenantId, d.DeviceId })
            .HasDatabaseName("ix_data_source_metadata_tenant_device")
            .IsUnique();

        entity
            .HasIndex(d => d.IsArchived)
            .HasDatabaseName("ix_data_source_metadata_is_archived");

        entity
            .HasIndex(d => d.CreatedAt)
            .HasDatabaseName("ix_data_source_metadata_created_at");
    }
}

internal sealed class ConnectorResetJobEntityConfiguration : IEntityTypeConfiguration<ConnectorResetJobEntity>
{
    public void Configure(EntityTypeBuilder<ConnectorResetJobEntity> entity)
    {
        entity
            .HasIndex(j => j.TenantId)
            .HasDatabaseName("ix_connector_reset_jobs_tenant");

        entity
            .HasIndex(j => j.State)
            .HasDatabaseName("ix_connector_reset_jobs_state");
    }
}

internal sealed class GoogleHealthGrantHolderEntityConfiguration : IEntityTypeConfiguration<GoogleHealthGrantHolderEntity>
{
    public void Configure(EntityTypeBuilder<GoogleHealthGrantHolderEntity> entity)
    {
        entity.HasIndex(e => e.AccountKey).HasDatabaseName("ix_google_health_grant_holders_account_key");
        entity.HasOne<TenantEntity>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
