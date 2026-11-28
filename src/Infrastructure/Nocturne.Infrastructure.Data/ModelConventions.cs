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
        foreach (var entity in TimestampIndexedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(IObservationTimestamped.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_timestamp")
                .IsDescending();
        }

        // Every table SourceTimestampIndexedEntities names gets the index, whether or not a connector
        // watermarks it. The connector watermark -- V4RepositoryBase.GetLatestTimestampAsync and
        // ObservedHealthSeriesRepository.GetLatestTimestampAsync -- asks for the newest timestamp of
        // one tenant and one data source. With neither column leading, the planner walks
        // ix_<table>_timestamp backwards across every other tenant's rows, so a tenant whose newest
        // row for that source is old reads most of the table to return one value. The deleted_at
        // filter keeps the scan index-only: soft-deleted rows left in the index have to be skipped a
        // heap fetch at a time, which is why it is not a size saving.
        foreach (var entity in SourceTimestampIndexedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(ISourcedEntity.DataSource),
                    nameof(IObservationTimestamped.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_source_timestamp")
                .IsDescending(false, false, true)
                .HasFilter("deleted_at IS NULL");
        }

        // The DataSource = source part of a source's count (SourceFilter.Parts) on a table the
        // source-timestamp index cannot cover: its only (tenant_id, data_source) index is the partial sync
        // key, which a count does not imply.
        foreach (var entity in SourceCountedWithoutTimestampEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(ITenantScoped.TenantId), nameof(ISourcedEntity.DataSource))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_source")
                .HasFilter("deleted_at IS NULL");
        }

        // The connector page's per-source count (CountsBySourceAsync, every table in V4RecordTables.All)
        // and the stats over V4RecordTables.Counted read the rows only their Device names as ranges
        // of this index (SourceFilter.Parts). Without it each read goes through the tenant's rows of
        // that table, for every connector on every page load. Rows a connector writes usually carry
        // its id in both handles, so the ranges that leave out DataSource = Device are usually empty.
        foreach (var entity in V4RecordTables.All
                     .Select(t => t.Entity)
                     .Where(typeof(ISourcedEntity).IsAssignableFrom)
                     .Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(ISourcedEntity.Device),
                    nameof(ISourcedEntity.DataSource))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_device_source")
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

        // A tenant's range reads by timestamp. Not partial on deleted_at, unlike the list indexes above,
        // because the window reads over some of these tables take deleted rows too. Without it a deleted-inclusive window read of the largest tenant
        // walks the instance-wide ix_<table>_timestamp past every other tenant's rows: on a tenant of
        // 100k bolus calculations, half the instance, 325 ms and 127k buffers for a 100k-row read
        // against 84 ms and 2k with it. Named for the column, which is start_timestamp on the span tables.
        foreach (var entity in TenantTimestampIndexedEntities.Select(t => modelBuilder.Entity(t)))
        {
            var column = entity.Property(nameof(IObservationTimestamped.Timestamp)).Metadata.GetColumnName();
            var index = entity.HasIndex(nameof(ITenantScoped.TenantId), nameof(IObservationTimestamped.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_{column}");
            if (!TenantTimestampAscending.ContainsKey(entity.Metadata.ClrType))
                index.IsDescending(false, true);
        }

        // A table with a list index takes that index's columns, so a live read keeps the partial list
        // index, the smaller of the two once any row is deleted (until then they hold the same entries),
        // and only a read that takes deleted rows needs this one. With fewer columns this index would be
        // the smaller, and a live read ordered by timestamp alone would walk it past deleted rows. Named
        // in HasIndex because EF keys an unnamed index by its columns, which the list index already holds.
        foreach (var entity in V4DeletedInclusiveWindowEntities.Where(V4SortedListEntities.Contains).Select(t => modelBuilder.Entity(t)))
        {
            var column = entity.Property(nameof(IObservationTimestamped.Timestamp)).Metadata.GetColumnName();
            var name = $"ix_{entity.Metadata.GetTableName()}_tenant_{column}_id_any";
            entity.HasIndex([nameof(ITenantScoped.TenantId), nameof(IObservationTimestamped.Timestamp), nameof(IIdentified.Id)], name)
                .HasDatabaseName(name)
                .IsDescending(false, true, true);
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

        // Every soft-deleted row, for the isValid:false upload paths, which ask whichever way a row
        // was deleted (GetDeletedLegacyIdsAsync, DecomposedTreatmentTable.DeletedAsync). Without it
        // the lookup walks every tenant's deleted rows through ix_<table>_deleted_at: 133 ms and 49k
        // buffers for 200 ids on a tenant of 1.5M sensor_glucose rows, 4% deleted, on an instance
        // twice that. The tombstone index above cannot be widened to serve it: EF folds the
        // tombstone arm of WhereBlocksRecreation to a bare deleted_by_user, which only the
        // user-tombstone index's filter implies.
        foreach (var entity in DeletedLegacyIdLookupEntities.Select(t => modelBuilder.Entity(t)))
        {
            var name = $"ix_{entity.Metadata.GetTableName()}_tenant_legacy_id_deleted";
            entity.HasIndex([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)], name)
                .HasDatabaseName(name)
                .HasFilter($"{LegacyIdColumn(entity)} IS NOT NULL AND deleted_at IS NOT NULL");
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
