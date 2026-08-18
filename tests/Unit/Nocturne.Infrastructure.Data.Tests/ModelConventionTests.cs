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
    public void EveryV4RecordTable_HasACorrelationIdIndex() =>
        AssertFamily(
            NocturneDbContext.V4LegacyIdRecordEntities,
            "_correlation_id",
            i => Columns(i).SequenceEqual([nameof(IV4Entity.CorrelationId)])
                && !i.IsUnique
                && i.GetFilter() is null);

    [Fact]
    public void EverySnapshotTable_HasAPlainLegacyIdLookup() =>
        AssertFamily(
            NocturneDbContext.V4SnapshotEntities,
            "_legacy_id",
            i => Columns(i).SequenceEqual([nameof(IV4Entity.LegacyId)])
                && !i.IsUnique
                && i.GetFilter() is null);

    /// <summary>
    /// EF drops this one as redundant against the partial sync-id index unless it is declared — see
    /// <see cref="NocturneDbContext.V4SnapshotEntities"/>.
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
        using var ctx = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
                .Options)
        { TenantId = Guid.NewGuid() };

        return ctx.GetService<IDesignTimeModel>().Model;
    }
}
