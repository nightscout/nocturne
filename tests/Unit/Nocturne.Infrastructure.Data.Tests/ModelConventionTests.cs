using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.ValueGenerators;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins the convention loops in <see cref="NocturneDbContext"/>. Each index loop walks a list, so an
/// emptied list would leave a whole family of indexes unemitted and every shape assertion below would
/// pass vacuously — hence the non-empty check on the list itself.
/// </summary>
[Trait("Category", "Unit")]
public class ModelConventionTests
{
    [Fact]
    public void EveryGeneratedGuidKey_GetsTheV7Generator()
    {
        var keys = Model().GetEntityTypes()
            .Select(e => e.FindPrimaryKey())
            .Where(k => k is { Properties.Count: 1 })
            .Select(k => k!.Properties[0])
            .Where(p => p.ClrType == typeof(Guid) && p.ValueGenerated == ValueGenerated.OnAdd)
            .ToList();

        keys.Should().HaveCountGreaterThan(80,
            "almost every table in the schema is keyed on a generated Guid");

        keys.Where(p => Generator(p) is not GuidV7ValueGenerator)
            .Select(p => p.DeclaringType.ShortName())
            .Should().BeEmpty(
                "a key EF generates has to get a time-ordered v7 value rather than whichever Guid shape the provider happens to default to");
    }

    [Fact]
    public void TheDerivedLists_HoldEveryMappedEntityThatCarriesTheirMarker()
    {
        var mapped = Model().GetEntityTypes().Select(e => e.ClrType).ToList();

        NocturneDbContext.V4LegacyIdRecordEntities.Should().BeEquivalentTo(Marked<IV4Entity>(mapped));
        NocturneDbContext.LegacyIdKeyedEntities.Should().BeEquivalentTo(Marked<ILegacyIdentified>(mapped));
        NocturneDbContext.SyncDedupedEntities.Should().BeEquivalentTo(Marked<ISyncDedupable>(mapped));
        NocturneDbContext.V4ProfileNamedEntities.Should().BeEquivalentTo(Marked<IProfileScopedEntity>(mapped));
    }

    /// <summary>
    /// The family is read off the model's timestamped tables, not off the registry it is derived from,
    /// so a timestamped table the registry lacks is reported here rather than left without its index.
    /// </summary>
    [Fact]
    public void TheTimestampIndexedRecords_AreEveryTimestampedRecordButTheTwoSpanTables()
    {
        var timestamped = Marked<IObservationTimestamped>(Model().GetEntityTypes().Select(e => e.ClrType));

        NocturneDbContext.TimestampIndexedEntities.Should().BeEquivalentTo(
            timestamped.Except([typeof(TempBasalEntity), typeof(ProfileActivationEntity)]),
            "a timestamped record table gets its timestamp index by its marker, and only a span table is left without it");

        NocturneDbContext.TimestampIndexedEntities.Should().Contain(
            [typeof(HeartRateEntity), typeof(StepCountEntity), typeof(TherapySettingsEntity), typeof(SensorGlucoseEntity)],
            "the health series are timestamped records outside the V4 marker, and the profile tables are not event series");

        NocturneDbContext.TimestampIndexedEntities.Should().NotContain(
            [typeof(TempBasalEntity), typeof(ProfileActivationEntity), typeof(StateSpanEntity), typeof(BodyWeightEntity)],
            "a span table is indexed on its start, and a table with no observation timestamp has no such column");
    }

    [Fact]
    public void EveryTimestampIndexedRecord_HasANewestFirstTimestampIndex() =>
        AssertFamily(
            NocturneDbContext.TimestampIndexedEntities,
            "_timestamp",
            i => Columns(i).SequenceEqual([nameof(IObservationTimestamped.Timestamp)])
                && !i.IsUnique
                && i.IsDescending is { Count: 0 }
                && i.GetFilter() is null);

    [Fact]
    public void EveryLegacyIdKeyedTable_HasTheLiveLegacyIdUniqueIndex() =>
        AssertFamily(
            NocturneDbContext.LegacyIdKeyedEntities,
            "_tenant_legacy_id",
            i => Columns(i).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)])
                && i.IsUnique
                && i.GetFilter() == $"{i.Properties[1].GetColumnName()} IS NOT NULL AND deleted_at IS NULL");

    [Fact]
    public void EveryLegacyIdKeyedTable_HasTheUserTombstoneLegacyIdIndex() =>
        AssertFamily(
            NocturneDbContext.LegacyIdKeyedEntities,
            "_tenant_legacy_id_user_deleted",
            i => Columns(i).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)])
                && !i.IsUnique
                && i.GetFilter() == $"{i.Properties[1].GetColumnName()} IS NOT NULL AND deleted_by_user");

    /// <summary>
    /// The isValid:false upload paths look up every soft-deleted row under a legacy id, whoever
    /// deleted it, so the index they probe has to hold system-swept rows as well as user tombstones.
    /// </summary>
    [Fact]
    public void EveryDeletedLegacyIdLookupTable_HasTheDeletedRowLegacyIdIndex() =>
        AssertFamily(
            NocturneDbContext.DeletedLegacyIdLookupEntities,
            "_tenant_legacy_id_deleted",
            i => Columns(i).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)])
                && !i.IsUnique
                && i.GetFilter() == $"{i.Properties[1].GetColumnName()} IS NOT NULL AND deleted_at IS NOT NULL");

    [Fact]
    public void EverySyncDedupedTable_HasTheUserTombstoneSyncKeyIndex() =>
        AssertFamily(
            NocturneDbContext.SyncDedupedEntities,
            "_tenant_source_sync_id_user_deleted",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(ISyncDedupable.DataSource),
                    nameof(ISyncDedupable.SyncIdentifier)])
                && !i.IsUnique
                && i.GetFilter() == "sync_identifier IS NOT NULL AND deleted_by_user");

    /// <summary>
    /// Every sourced record table with a timestamp has the tenant, source and timestamp index. Of those,
    /// <see cref="Nocturne.Infrastructure.Data.Repositories.V4.V4RepositoryBase{TModel,TEntity}"/>
    /// and the health series repository watermark each connector sync on the newest timestamp for one
    /// tenant and data source, and the rest (therapy settings, the profile schedules, sensor state
    /// reports, profile activations) share the index for a source's count. The
    /// filter is load-bearing rather than a size saving: soft-deleted rows left in the index have
    /// to be skipped a heap fetch at a time, which costs the index-only scan.
    /// </summary>
    [Fact]
    public void EverySourceTimestampIndexedTable_HasTheTenantSourceTimestampIndex() =>
        AssertFamily(
            NocturneDbContext.SourceTimestampIndexedEntities,
            "_tenant_source_timestamp",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(ISourcedEntity.DataSource),
                    nameof(IObservationTimestamped.Timestamp)])
                && !i.IsUnique
                && i.IsDescending is not null
                && i.IsDescending.SequenceEqual([false, false, true])
                && i.GetFilter() == "deleted_at IS NULL");

    [Fact]
    public void EverySourceCountedTableWithoutATimestamp_HasTheTenantSourceIndex() =>
        AssertFamily(
            NocturneDbContext.SourceCountedWithoutTimestampEntities,
            "_tenant_source",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(ISourcedEntity.DataSource)])
                && !i.IsUnique
                && i.GetFilter() == "deleted_at IS NULL");

    /// <summary>
    /// A source's count and stats read the rows only their device names (SourceFilter.Parts) off this
    /// index, on every sourced table the record registry holds.
    /// </summary>
    [Fact]
    public void EverySourcedRecordTable_HasTheTenantDeviceSourceIndex() =>
        AssertFamily(
            [.. V4RecordTables.All.Select(t => t.Entity).Where(typeof(ISourcedEntity).IsAssignableFrom)],
            "_tenant_device_source",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(ISourcedEntity.Device),
                    nameof(ISourcedEntity.DataSource)])
                && !i.IsUnique
                && i.GetFilter() == "deleted_at IS NULL");

    /// <summary>
    /// GetLatestAsync and the pump GetTimelineAsync pin no data source, so the watermark index above
    /// cannot serve them.
    /// </summary>
    [Fact]
    public void EverySortedListTable_HasTheTenantLeadingSortIndex() =>
        AssertFamily(
            NocturneDbContext.V4SortedListEntities,
            "_tenant_timestamp_id",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(IV4TimeSeriesEntity.Timestamp),
                    nameof(IIdentified.Id)])
                && !i.IsUnique
                && i.IsDescending is not null
                && i.IsDescending.SequenceEqual([false, true, true])
                && i.GetFilter() == "deleted_at IS NULL");

    [Fact]
    public void EveryDeduplicatedListTable_HasTheTenantLeadingSortIndexOverTheRowsReadsShow() =>
        AssertFamily(
            NocturneDbContext.V4VisibleSortedListEntities,
            "_tenant_visible_timestamp_id",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(IV4TimeSeriesEntity.Timestamp),
                    nameof(IIdentified.Id)])
                && !i.IsUnique
                && i.IsDescending is not null
                && i.IsDescending.SequenceEqual([false, true, true])
                && i.GetFilter() == "deleted_at IS NULL AND NOT is_duplicate");

    /// <summary>
    /// A filter on <c>deleted_at</c> would leave the deleted rows these reads take out of the index.
    /// </summary>
    [Fact]
    public void EveryDeletedInclusiveWindowTable_HasAnUnfilteredTenantTimestampIndex()
    {
        NocturneDbContext.V4DeletedInclusiveWindowEntities.Should().NotBeEmpty();
        var model = Model();

        NocturneDbContext.V4DeletedInclusiveWindowEntities.Select(model.FindEntityType)
            .Where(e => e is null || !e.GetIndexes().Any(i => IsDeletedInclusiveWindowIndex(e, i)))
            .Select(e => e?.ShortName() ?? "<unmapped>")
            .Should().BeEmpty("a window read over deleted rows needs a tenant-led timestamp index that holds them");
    }

    [Fact]
    public void TheTenantTimestampIndexedTables_AreTheTimestampedRecordsWithoutAListIndex()
    {
        NocturneDbContext.TenantTimestampIndexedEntities.Should().BeEquivalentTo(
            new[]
            {
                typeof(SensorGlucoseEntity), typeof(BolusEntity), typeof(CarbIntakeEntity),
                typeof(BolusCalculationEntity), typeof(DeviceEventEntity), typeof(BGCheckEntity),
                typeof(NoteEntity), typeof(TempBasalEntity), typeof(ProfileActivationEntity),
                typeof(HeartRateEntity), typeof(StepCountEntity), typeof(TherapySettingsEntity),
                typeof(DeviceStatusExtrasEntity),
            },
            "a timestamped record table that takes no list index gets the unfiltered index by the registry, not by where its author declared it");

        NocturneDbContext.TenantTimestampIndexedEntities.Should().NotContain(
            NocturneDbContext.V4SortedListEntities,
            "a table with a list index takes the list index's columns in its _id_any twin");

        NocturneDbContext.TenantTimestampAscending.Keys.Should().BeEquivalentTo(
            [typeof(HeartRateEntity), typeof(StepCountEntity)]);
    }

    [Fact]
    public void EveryTenantTimestampIndexedTable_HasTheUnfilteredTenantTimestampIndex()
    {
        NocturneDbContext.TenantTimestampIndexedEntities.Should().NotBeEmpty();
        var model = Model();

        NocturneDbContext.TenantTimestampIndexedEntities.Select(model.FindEntityType)
            .Where(e => e is null || !e.GetIndexes().Any(i => IsTenantTimestampIndex(e, i)))
            .Select(e => e?.ShortName() ?? "<unmapped>")
            .Should().BeEmpty("each is named for its timestamp column, newest first unless listed ascending");
    }

    private static bool IsTenantTimestampIndex(IEntityType entity, IIndex index)
    {
        var column = entity.FindProperty(nameof(IObservationTimestamped.Timestamp))!.GetColumnName();
        var ascending = NocturneDbContext.TenantTimestampAscending.ContainsKey(entity.ClrType);
        return index.GetDatabaseName() == $"ix_{entity.GetTableName()}_tenant_{column}"
            && Columns(index).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(IObservationTimestamped.Timestamp)])
            && !index.IsUnique
            && index.GetFilter() is null
            && (ascending
                ? index.IsDescending is not { Count: > 0 }
                : index.IsDescending is not null && index.IsDescending.SequenceEqual([false, true]));
    }

    /// <summary>
    /// A table with a list index takes that index's columns, so the partial list index stays the
    /// smaller for a live read; see <see cref="ModelConventions"/>.
    /// </summary>
    private static bool IsDeletedInclusiveWindowIndex(IEntityType entity, IIndex index)
    {
        var prefix = $"ix_{entity.GetTableName()}_tenant_{entity.FindProperty(nameof(IObservationTimestamped.Timestamp))!.GetColumnName()}";
        var (name, columns, descending) = NocturneDbContext.V4SortedListEntities.Contains(entity.ClrType)
            ? ($"{prefix}_id_any",
                new[] { nameof(ITenantScoped.TenantId), nameof(IObservationTimestamped.Timestamp), nameof(IIdentified.Id) },
                new[] { false, true, true })
            : (prefix,
                new[] { nameof(ITenantScoped.TenantId), nameof(IObservationTimestamped.Timestamp) },
                new[] { false, true });
        return index.GetDatabaseName() == name
            && Columns(index).SequenceEqual(columns)
            && !index.IsUnique
            && index.IsDescending is not null
            && index.IsDescending.SequenceEqual(descending)
            && index.GetFilter() is null;
    }

    [Fact]
    public void EveryCorrelatedTable_HasACorrelationIdIndex() =>
        AssertFamily(
            NocturneDbContext.V4CorrelationIndexedEntities,
            "_correlation_id",
            i => Columns(i).SequenceEqual([nameof(IV4Entity.CorrelationId)])
                && !i.IsUnique
                && i.GetFilter() is null);

    /// <summary>
    /// A list, not the model, drives the loop above, so a new correlated table would ship without
    /// the lookup index. Discovery is by property name, which only the decomposition correlation
    /// bears — the request-trace identifier the audit and compatibility-proxy tables carry in
    /// their own <c>correlation_id</c> columns is <c>TraceId</c> in the model.
    /// </summary>
    [Fact]
    public void EveryTableCarryingTheDecompositionCorrelation_IsInTheCorrelationIndexFamily()
    {
        var declared = Model().GetEntityTypes()
            .Where(e => e.FindProperty(nameof(IV4Entity.CorrelationId)) is not null)
            .Select(e => e.ClrType)
            .ToList();

        declared.Should().HaveCountGreaterThan(15,
            "an empty set would let the assertion below pass vacuously");

        declared.Except(NocturneDbContext.V4CorrelationIndexedEntities)
            .Select(t => t.Name)
            .Should().BeEmpty("every table carrying the decomposition correlation needs its lookup index");
    }

    /// <summary>
    /// The snapshot tables' other tenant-leading indexes are partial, and EF drops its conventional
    /// tenant index as redundant against them, so a table neither history-paged nor given
    /// IX_&lt;table&gt;_tenant_id would leave a tenant-wide scan nothing to read.
    /// </summary>
    [Fact]
    public void EverySnapshotTable_HasAnUnfilteredTenantLeadingIndex()
    {
        NocturneDbContext.V4SnapshotEntities.Should().NotBeEmpty();
        var model = Model();

        NocturneDbContext.V4SnapshotEntities.Select(model.FindEntityType)
            .Where(e => e is null
                || !e.GetIndexes().Any(i =>
                    i.Properties[0].Name == nameof(ITenantScoped.TenantId) && i.GetFilter() is null))
            .Select(e => e?.ShortName() ?? "<unmapped>")
            .Should().BeEmpty("a tenant-wide scan of a snapshot table needs an unfiltered tenant-leading index");
    }

    [Fact]
    public void EverySyncDedupedTable_HasThePartialUniqueUpsertKey() =>
        AssertFamily(
            NocturneDbContext.SyncDedupedEntities,
            "_tenant_source_sync_id",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(ISyncDedupable.DataSource),
                    nameof(ISyncDedupable.SyncIdentifier)])
                && i.IsUnique
                && i.GetFilter() == "sync_identifier IS NOT NULL AND deleted_at IS NULL");

    [Fact]
    public void EveryProfileTable_HasAProfileNameIndex() =>
        AssertFamily(
            NocturneDbContext.V4ProfileNamedEntities,
            "_profile_name",
            i => Columns(i).SequenceEqual([nameof(BasalScheduleEntity.ProfileName)]) && !i.IsUnique);

    [Fact]
    public void EveryProfileScheduleTable_HasTheTenantProfileOrderingIndex() =>
        AssertFamily(
            NocturneDbContext.V4ProfileScheduleEntities,
            "_tenant_profile_timestamp",
            i => Columns(i).SequenceEqual([
                    nameof(ITenantScoped.TenantId),
                    nameof(BasalScheduleEntity.ProfileName),
                    nameof(IV4TimeSeriesEntity.Timestamp)])
                && !i.IsUnique
                && i.IsDescending is not null
                && i.IsDescending.SequenceEqual([false, false, true]));

    /// <summary>
    /// <see cref="Nocturne.Infrastructure.Data.Extensions.PurgeExtensions"/> lifts the soft-delete
    /// filter by key, and <c>IgnoreQueryFilters</c> ignores a key the entity does not declare — so
    /// folding the two predicates back into one filter would silently restore the purge skip.
    /// </summary>
    [Fact]
    public void EverySoftDeletableEntity_DeclaresTheTenantAndSoftDeleteFiltersSeparately()
    {
        var softDeletable = Model().GetEntityTypes()
            .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType)
                     && typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .ToList();

        softDeletable.Should().HaveCountGreaterThan(20,
            "an empty set would let every assertion below pass vacuously");

        softDeletable
            .Where(e => e.FindDeclaredQueryFilter(NocturneDbContext.SoftDeleteFilterKey) is null
                     || e.FindDeclaredQueryFilter(NocturneDbContext.TenantFilterKey) is null)
            .Select(e => e.ShortName())
            .Should().BeEmpty("a purge has to lift the soft-delete filter without lifting tenant isolation");
    }

    /// <summary>
    /// A list, not the model, drives the loop, and no convention would put an entry back: a
    /// dropped one silently returns its column to a database default of NULL. A wrongly grouped
    /// entry throws while the model is built, so only the value and the mapping are asserted here.
    /// </summary>
    [Fact]
    public void EveryListedTimestampColumn_KeepsItsCurrentTimestampDefault()
    {
        var listed = NocturneDbContext.CurrentTimestampDefaults
            .SelectMany(g => g.Entities.Select(t => (Entity: t, g.Property)))
            .ToList();

        listed.Should().HaveCountGreaterThan(15,
            "a loop over an empty list emits nothing, and the assertion below would then pass vacuously");

        var model = Model();

        listed
            .Where(p => model.FindEntityType(p.Entity)?.FindProperty(p.Property) is not { } property
                || property.GetDefaultValueSql() != "CURRENT_TIMESTAMP")
            .Select(p => $"{p.Entity.Name}.{p.Property}")
            .Should().BeEmpty("every listed column needs the default on its own mapped property");
    }

    [Fact]
    public void EveryMarkerDeclaredTimestampColumn_HasACurrentTimestampDefault()
    {
        (Type Marker, string Property)[] markers =
        [
            (typeof(ISystemCreated), nameof(ISystemCreated.SysCreatedAt)),
            (typeof(ISystemTimestamped), nameof(ISystemTimestamped.SysUpdatedAt)),
            (typeof(IEntityCreated), nameof(IEntityCreated.CreatedAt)),
            (typeof(IEntityTimestamped), nameof(IEntityTimestamped.UpdatedAt)),
        ];

        var columns = Model().GetEntityTypes()
            .SelectMany(e => markers
                .Where(m => m.Marker.IsAssignableFrom(e.ClrType))
                .Select(m => (Entity: e, m.Property)))
            .ToList();

        columns.Should().HaveCountGreaterThan(90,
            "every record, snapshot, schedule and identity table declares a timestamp marker");

        columns
            .Where(c => c.Entity.FindProperty(c.Property)?.GetDefaultValueSql() != "CURRENT_TIMESTAMP")
            .Select(c => $"{c.Entity.ClrType.Name}.{c.Property}")
            .Should().BeEmpty(
                "a write that bypasses SaveChanges would otherwise store 0001-01-01 in the column");
    }

    private static void AssertFamily(
        IReadOnlyList<Type> entities,
        string suffix,
        Func<IIndex, bool> shape,
        string prefix = "ix_")
    {
        entities.Should().NotBeEmpty(
            "a loop over an empty list emits nothing, and every shape assertion would then pass vacuously");

        var model = Model();

        entities.Select(model.FindEntityType)
            .Where(e => e is null
                || !e.GetIndexes().Any(i =>
                    i.GetDatabaseName() == $"{prefix}{e.GetTableName()}{suffix}" && shape(i)))
            .Select(e => e?.ShortName() ?? "<unmapped>")
            .Should().BeEmpty(
                "every listed table needs {0}<table>{1} in the conventional shape", prefix, suffix);
    }

    private static IEnumerable<string> Columns(IIndex index) => index.Properties.Select(p => p.Name);

    private static ValueGenerator? Generator(IProperty property) =>
        property.GetValueGeneratorFactory()?.Invoke(property, property.DeclaringType);

    /// <summary>
    /// Value generators and index sort order live only on the design-time model; the read-optimized
    /// runtime model throws for both.
    /// </summary>
    private static IModel Model()
    {
        using var ctx = OfflineDbContext.Create();

        return ctx.GetService<IDesignTimeModel>().Model;
    }

    private static IEnumerable<Type> Marked<TMarker>(IEnumerable<Type> mapped) =>
        mapped.Where(t => typeof(TMarker).IsAssignableFrom(t));
}
