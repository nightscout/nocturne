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
    public void EveryV4RecordTable_HasANewestFirstTimestampIndex() =>
        AssertFamily(
            NocturneDbContext.V4TimeSeriesRecordEntities,
            "_timestamp",
            i => Columns(i).SequenceEqual([nameof(IV4TimeSeriesEntity.Timestamp)])
                && !i.IsUnique
                && i.IsDescending is { Count: 0 }
                && i.GetFilter() is null);

    [Fact]
    public void EveryV4RecordTable_HasTheLiveLegacyIdUniqueIndex() =>
        AssertFamily(
            NocturneDbContext.V4LegacyIdRecordEntities,
            "_tenant_legacy_id",
            i => Columns(i).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(IV4Entity.LegacyId)])
                && i.IsUnique
                && i.GetFilter() == "legacy_id IS NOT NULL AND deleted_at IS NULL");

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
    /// EF drops this one as redundant against the tenant-leading partial indexes unless it is
    /// declared — see <see cref="NocturneDbContext.V4SnapshotEntities"/>.
    /// </summary>
    [Fact]
    public void EverySnapshotTable_KeepsTheUnfilteredTenantIndex() =>
        AssertFamily(
            NocturneDbContext.V4SnapshotEntities,
            "_tenant_id",
            i => Columns(i).SequenceEqual([nameof(ITenantScoped.TenantId)])
                && !i.IsUnique
                && i.GetFilter() is null,
            prefix: "IX_");

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

        listed.Should().HaveCountGreaterThan(40,
            "a loop over an empty list emits nothing, and the assertion below would then pass vacuously");

        var model = Model();

        listed
            .Where(p => model.FindEntityType(p.Entity)?.FindProperty(p.Property) is not { } property
                || property.GetDefaultValueSql() != "CURRENT_TIMESTAMP")
            .Select(p => $"{p.Entity.Name}.{p.Property}")
            .Should().BeEmpty("every listed column needs the default on its own mapped property");
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
}
