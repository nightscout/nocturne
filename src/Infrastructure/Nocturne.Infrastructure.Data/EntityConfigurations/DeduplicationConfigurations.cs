using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class LinkedRecordEntityConfiguration : IEntityTypeConfiguration<LinkedRecordEntity>
{
    public void Configure(EntityTypeBuilder<LinkedRecordEntity> entity)
    {
        // A canonical id is minted per group, so it alone narrows a lookup to that group's few links;
        // leading with record_type, or adding is_primary, only made the index bigger.
        entity
            .HasIndex(l => l.CanonicalId)
            .HasDatabaseName("ix_linked_records_canonical");

        entity
            .HasIndex(l => new { l.TenantId, l.RecordType, l.RecordId })
            .IsUnique()
            .HasDatabaseName("ix_linked_records_tenant_type_id");

        // DuplicateReconciler.ReconcileNewLinksAsync pages the tenant's links by creation order.
        // The unique index above leads with tenant_id but then record_type, so it can only supply
        // the tenant and the rest is a filter plus a sort of everything that tenant owns.
        entity
            .HasIndex(l => new { l.TenantId, l.SysCreatedAt })
            .HasDatabaseName("ix_linked_records_tenant_created");

        // Every read of this table is tenant-scoped, by the global query filter and again by the
        // tenant_isolation RLS policy, so leading on record_type made the window scan span all
        // tenants -- 41,414 index tuples read per scan, the worst ratio in the schema. Serves the
        // dedup window reads whether or not they also pin is_primary, which is selective enough to
        // leave as a filter.
        entity
            .HasIndex(l => new { l.TenantId, l.RecordType, l.SourceTimestamp })
            .HasDatabaseName("ix_linked_records_tenant_type_timestamp");

        // ix_linked_records_tenant_type_not_primary, the keyset of a type's non-primary links, is
        // created in raw SQL by AddCompositePerformanceIndexes and so the model does not carry it.

        entity.Property(e => e.IsPrimary).HasDefaultValue(false);
    }
}

internal sealed class DedupReconcileStateEntityConfiguration : IEntityTypeConfiguration<DedupReconcileStateEntity>
{
    public void Configure(EntityTypeBuilder<DedupReconcileStateEntity> entity)
    {
        // One row per tenant, keyed on the tenant id rather than an Id of its own.
        entity.HasKey(e => e.TenantId);
    }
}
