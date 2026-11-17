using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.EntityConfigurations;

internal sealed class StateSpanEntityConfiguration : IEntityTypeConfiguration<StateSpanEntity>
{
    public void Configure(EntityTypeBuilder<StateSpanEntity> entity)
    {
        entity
            .HasIndex(s => s.StartTimestamp)
            .HasDatabaseName("ix_state_spans_start_timestamp")
            .IsDescending();

        entity
            .HasIndex(s => s.Category)
            .HasDatabaseName("ix_state_spans_category");

        entity
            .HasIndex(s => s.EndTimestamp)
            .HasDatabaseName("ix_state_spans_end_timestamp")
            .HasFilter("end_timestamp IS NULL"); // Partial index for active spans

        entity
            .HasIndex(s => new { s.Category, s.StartTimestamp })
            .HasDatabaseName("ix_state_spans_category_start")
            .IsDescending(false, true);

        entity
            .HasIndex(s => s.Source)
            .HasDatabaseName("ix_state_spans_source");

        // Connector resume watermarks: the latest start of one data source, activity and non-activity,
        // each probing this index once per category (see StateSpanRepository.GetLatestStartAsync).
        // Tenant leads because a source id is the same string installation-wide, so
        // ix_state_spans_source would walk every tenant's spans for that source.
        entity
            .HasIndex(s => new { s.TenantId, s.Source, s.Category, s.StartTimestamp })
            .HasDatabaseName("ix_state_spans_tenant_source_category_start")
            .IsDescending(false, false, false, true);

        // HistoryPage's (updated_at, id) order for the legacy treatment projection's state-span
        // categories, as the sys_updated_at history index serves the V4 record tables.
        entity
            .HasIndex(s => new { s.TenantId, s.Category, s.UpdatedAt, s.Id })
            .HasDatabaseName("ix_state_spans_tenant_category_updated_at")
            .HasFilter("deleted_at IS NULL");

        entity
            .HasIndex(s => new { s.TenantId, s.SupersededById })
            .HasDatabaseName("ix_state_spans_tenant_id_superseded_by_id");

        entity
            .HasOne<StateSpanEntity>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.SupersededById })
            .OnDelete(DeleteBehavior.SetNull);

        entity
            .HasIndex(nameof(ITenantScoped.TenantId), nameof(StateSpanEntity.StartTimestamp), nameof(IIdentified.Id))
            .HasDatabaseName("ix_state_spans_tenant_visible_start_id")
            .IsDescending(false, true, true)
            .HasFilter("deleted_at IS NULL AND NOT is_duplicate");

        // StateSpanRepository.ResolveDeletedAsync looks a deleted span up by id whichever way it was
        // deleted, and both legacy-id indexes ModelConventions gives it skip a system-deleted span.
        entity
            .HasIndex([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)], "ix_state_spans_tenant_legacy_id_any")
            .HasDatabaseName("ix_state_spans_tenant_legacy_id_any");

        NocturneDbContext.ScopeKeyToTenant(entity);
    }
}
