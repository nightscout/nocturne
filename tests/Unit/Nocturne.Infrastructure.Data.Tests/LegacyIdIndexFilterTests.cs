using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins the presence and the filter of the <c>(tenant_id, legacy_id)</c> uniqueness index that
/// backstops every legacy-id-keyed v4 table.
///
/// <see cref="Nocturne.Infrastructure.Data.Repositories.V4.V4RepositoryBase{TModel, TEntity}"/>
/// dedups a bulk create by reading the colliding legacy ids and then inserting the survivors, so
/// the index is the only thing standing between two overlapping imports and a duplicate row.
///
/// <see cref="Nocturne.Infrastructure.Data.Extensions.SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync"/>
/// deliberately lets a legacy id whose row was soft-deleted by a system sweep be re-imported:
/// resync inserts a fresh row and leaves the old one for audit. That only works if the old row
/// has dropped out of the uniqueness index, so an index that filters on <c>legacy_id IS NOT NULL</c>
/// alone turns the very next resync into a 23505 on that table.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyIdIndexFilterTests
{
    [Fact]
    public void EveryLegacyIdTable_HasATenantScopedUniqueIndex()
    {
        using var ctx = CreateContext();

        LegacyIdEntityTypes(ctx)
            .Where(e => !e.GetIndexes().Any(IsTenantLegacyIdUniqueIndex))
            .Select(e => e.GetTableName())
            .Should().BeEmpty(
                "a read-then-insert dedup with no uniqueness index lets two overlapping imports both insert the same legacy id");
    }

    [Fact]
    public void LegacyIdUniqueIndexes_ExcludeSoftDeletedRows()
    {
        using var ctx = CreateContext();

        LegacyIdEntityTypes(ctx)
            .SelectMany(e => e.GetIndexes())
            .Where(i => IsTenantLegacyIdUniqueIndex(i)
                && i.GetFilter()?.Contains("deleted_at IS NULL", StringComparison.Ordinal) != true)
            .Select(i => i.GetDatabaseName())
            .Should().BeEmpty(
                "a soft-deleted row must drop out of the index so a system-swept legacy id stays re-importable");
    }

    private static NocturneDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .Options)
        { TenantId = Guid.NewGuid() };

    /// <summary>
    /// Both facts pass vacuously if discovery drifts, so the count is asserted here rather than
    /// in either one.
    /// </summary>
    private static IReadOnlyList<IEntityType> LegacyIdEntityTypes(NocturneDbContext ctx)
    {
        var entityTypes = ctx.Model.GetEntityTypes()
            .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType)
                && e.FindProperty(nameof(IV4Entity.LegacyId)) is not null)
            .ToList();

        entityTypes.Should().HaveCountGreaterThan(10,
            "the v4 granular model spreads legacy ids across every time-series and schedule table");

        return entityTypes;
    }

    private static bool IsTenantLegacyIdUniqueIndex(IIndex index) =>
        index.IsUnique
        && index.Properties.Select(p => p.Name).SequenceEqual(
            [nameof(ITenantScoped.TenantId), nameof(IV4Entity.LegacyId)]);
}
