using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using static Nocturne.Infrastructure.Data.NocturneDbContext;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// The rules <see cref="NocturneDbContext"/> applies to a family of entity types at once, after each
/// type's own configuration in <c>EntityConfigurations</c>. A family is one of the context's lists or
/// the implementers of a marker interface.
/// </summary>
internal static class ModelConventions
{
    /// <summary>
    /// The index shapes the record tables share, where only the table-name stem differs.
    /// </summary>
    internal static void ConfigureSharedRecordIndexes(ModelBuilder modelBuilder)
    {
        foreach (var entity in V4TimeSeriesRecordEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(IV4TimeSeriesEntity.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_timestamp")
                .IsDescending();
        }

        // The connector watermark -- V4RepositoryBase.GetLatestTimestampAsync -- asks for the
        // newest timestamp of one tenant and one data source. With neither column leading, the
        // planner walks ix_<table>_timestamp backwards across every other tenant's rows, so a
        // tenant whose newest row for that source is old reads most of the table to return one
        // value. The deleted_at filter keeps the scan index-only: soft-deleted rows left in the
        // index have to be skipped a heap fetch at a time, which is why it is not a size saving.
        foreach (var entity in V4WatermarkedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(IV4TimeSeriesEntity.DataSource),
                    nameof(IV4TimeSeriesEntity.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_source_timestamp")
                .IsDescending(false, false, true)
                .HasFilter("deleted_at IS NULL");
        }

        // A V4 list reads one tenant newest-first by timestamp, then id on the tables that tie-break
        // on it. With no index leading (tenant_id, timestamp), a LIMIT read reads every live row and
        // sorts. GetLatestAsync and the pump GetTimelineAsync on a snapshot table walk the same index.
        foreach (var entity in V4SortedListEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(IV4TimeSeriesEntity.Timestamp),
                    nameof(IIdentified.Id))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_timestamp_id")
                .IsDescending(false, true, true)
                .HasFilter("deleted_at IS NULL");
        }

        // On a deduplicated table the index holds only the rows reads show, so an unbounded count is
        // an index-only scan and a range read never visits a duplicate's heap tuple. A read that skips
        // ReadVisibilityFilter cannot use it: it falls back to the (tenant_id, timestamp) index the
        // dedup read keeps on the larger tables, or to the global timestamp index.
        foreach (var entity in V4VisibleSortedListEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(IV4TimeSeriesEntity.Timestamp),
                    nameof(IIdentified.Id))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_visible_timestamp_id")
                .IsDescending(false, true, true)
                .HasFilter("deleted_at IS NULL AND NOT is_duplicate");
        }

        // The legacy-id uniqueness must drop soft-deleted rows, or the next resync of a
        // system-swept legacy id is a 23505 — see SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync.
        foreach (var entity in LegacyIdKeyedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_legacy_id")
                .IsUnique()
                .HasFilter($"{LegacyIdColumn(entity)} IS NOT NULL AND deleted_at IS NULL");
        }

        // GetBlockingLegacyIdsAsync also wants the user tombstones, which the unique index above
        // leaves out, so without this the lookup scans every row the tenant owns: 719 ms and 187k
        // buffers to find 200 ids on the largest production tenant. With it, the planner answers
        // the OR in WhereBlocksRecreation with a BitmapOr over the two partial indexes. Tombstones
        // only, not every soft-deleted row, because that is the arm the OR asks for; on production
        // that is 1.5% of sensor_glucose and at most 15% of any table. Named, because an unnamed HasIndex on the same
        // columns would reconfigure the unique index instead of adding this one.
        foreach (var entity in LegacyIdKeyedEntities.Select(t => modelBuilder.Entity(t)))
        {
            var name = $"ix_{entity.Metadata.GetTableName()}_tenant_legacy_id_user_deleted";
            entity.HasIndex([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)], name)
                .HasDatabaseName(name)
                .HasFilter($"{LegacyIdColumn(entity)} IS NOT NULL AND deleted_by_user");
        }

        // Matches HistoryPage's (sys_updated_at, id) order under the tenant filter, so a poll reads
        // its page off the index instead of sorting the tenant's whole table. Not partial on
        // deleted_at: a history read returns soft-deleted rows too (HistoryPage.IncludingDeleted).
        foreach (var entity in V4HistoryPagedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(ISystemTimestamped.SysUpdatedAt),
                    nameof(IIdentified.Id))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_history");
        }

        foreach (var entity in V4CorrelationIndexedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(IV4Entity.CorrelationId))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_correlation_id");
        }

        // The partial sync-id and legacy-id indexes lead with tenant_id, which makes EF drop the
        // auto-created tenant index as redundant, but a filtered index can't serve general
        // tenant-scoped scans (all pre-existing rows have NULL sync_identifier). A history-paged
        // table needs none: its unfiltered ix_<table>_tenant_history already leads with tenant_id.
        foreach (var entity in V4SnapshotEntities.Except(V4HistoryPagedEntities).Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(ITenantScoped.TenantId))
                .HasDatabaseName($"IX_{entity.Metadata.GetTableName()}_tenant_id");
        }

        foreach (var (type, property) in SparseForeignKeys)
        {
            var entity = modelBuilder.Entity(type);
            var column = entity.Property(property).Metadata.GetColumnName();
            entity.HasIndex(property)
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_{column}")
                .HasFilter($"{column} IS NOT NULL");
        }

        foreach (var entity in V4ProfileNamedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(BasalScheduleEntity.ProfileName))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_profile_name");
        }

        foreach (var entity in V4ProfileScheduleEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(BasalScheduleEntity.ProfileName),
                    nameof(IV4TimeSeriesEntity.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_profile_timestamp")
                .IsDescending(false, false, true);
        }

        foreach (var entity in SyncDedupedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(ISyncDedupable.DataSource),
                    nameof(ISyncDedupable.SyncIdentifier))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_source_sync_id")
                .IsUnique()
                .HasFilter("sync_identifier IS NOT NULL AND deleted_at IS NULL");
        }

        // The sync-key lookups also want the user tombstones the unique index above leaves out, for
        // the same reason as the legacy-id companion above: the planner BitmapOrs the two. Named, as there.
        foreach (var entity in SyncDedupedEntities.Select(t => modelBuilder.Entity(t)))
        {
            var name = $"ix_{entity.Metadata.GetTableName()}_tenant_source_sync_id_user_deleted";
            entity.HasIndex(
                    [
                        nameof(ITenantScoped.TenantId),
                        nameof(ISyncDedupable.DataSource),
                        nameof(ISyncDedupable.SyncIdentifier),
                    ],
                    name)
                .HasDatabaseName(name)
                .HasFilter("sync_identifier IS NOT NULL AND deleted_by_user");
        }
    }

    /// <summary>
    /// The column <see cref="ILegacyIdentified.LegacyId"/> maps to: <c>legacy_id</c>, except on
    /// <c>state_spans</c>, which keeps <c>original_id</c>.
    /// </summary>
    private static string LegacyIdColumn(EntityTypeBuilder entity)
        => entity.Property(nameof(ILegacyIdentified.LegacyId)).Metadata.GetColumnName();

    /// <summary>Keeps <see cref="IDuplicateFlagged.IsDuplicate"/> out of every save; see <see cref="IDuplicateFlagged"/>.</summary>
    internal static void ConfigureDuplicateFlags(ModelBuilder modelBuilder)
    {
        foreach (var flagged in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(IDuplicateFlagged).IsAssignableFrom(e.ClrType)).ToList())
        {
            var flag = modelBuilder.Entity(flagged.ClrType).Property(nameof(IDuplicateFlagged.IsDuplicate));
            flag.HasDefaultValue(false).ValueGeneratedNever();
            flag.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            flag.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        }
    }

    /// <summary>
    /// Applies <see cref="MarkerTimestampColumns"/> to every mapped entity declaring the marker,
    /// then <see cref="CurrentTimestampDefaults"/>.
    /// </summary>
    internal static void ConfigureCurrentTimestampDefaults(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(e => !e.IsOwned()).ToList())
        {
            foreach (var (marker, property) in MarkerTimestampColumns)
            {
                if (marker.IsAssignableFrom(entityType.ClrType))
                    modelBuilder.Entity(entityType.ClrType).Property(property).HasDefaultValueSql("CURRENT_TIMESTAMP");
            }
        }

        foreach (var (property, entities) in CurrentTimestampDefaults)
        {
            foreach (var entity in entities.Select(t => modelBuilder.Entity(t)))
            {
                entity.Property(property).HasDefaultValueSql("CURRENT_TIMESTAMP");
            }
        }
    }

    /// <summary>
    /// Adds FK constraints with ON DELETE CASCADE from every ITenantScoped entity's
    /// TenantId column to the tenants table. This ensures tenant deletion cascades to
    /// all tenant-scoped data instead of silently orphaning rows.
    /// An entity that also exposes a <c>Tenant</c> reference has that navigation bound to this
    /// relationship; configured without it, EF treats the navigation as a second relationship and
    /// gives it a shadow foreign key of its own.
    /// </summary>
    internal static void ConfigureTenantCascadeDeletes(ModelBuilder modelBuilder)
    {
        // The conventional name of the tenant reference on the entities that expose one.
        const string TenantNavigation = "Tenant";

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
                continue;

            var navigation = entityType.ClrType.GetProperty(TenantNavigation)?.PropertyType == typeof(TenantEntity)
                ? TenantNavigation
                : null;

            modelBuilder.Entity(entityType.ClrType)
                .HasOne(typeof(TenantEntity), navigation)
                .WithMany()
                .HasForeignKey(nameof(ITenantScoped.TenantId))
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
