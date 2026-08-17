using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Pins the filter on every <c>(tenant_id, legacy_id)</c> uniqueness index.
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
    public void LegacyIdUniqueIndexes_ExcludeSoftDeletedRows()
    {
        using var ctx = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
                .Options)
        { TenantId = Guid.NewGuid() };

        var legacyIdIndexes = ctx.Model.GetEntityTypes()
            .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType))
            .SelectMany(e => e.GetIndexes())
            .Where(i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(
                    [nameof(ITenantScoped.TenantId), nameof(IV4Entity.LegacyId)]))
            .ToList();

        // Without this the assertion below passes vacuously the moment the discovery drifts.
        legacyIdIndexes.Should().HaveCountGreaterThan(10,
            "the model carries a legacy-id uniqueness index on every soft-deletable v4 table");

        legacyIdIndexes
            .Where(i => i.GetFilter()?.Contains("deleted_at IS NULL", StringComparison.Ordinal) != true)
            .Select(i => i.GetDatabaseName())
            .Should().BeEmpty(
                "a soft-deleted row must drop out of the index so a system-swept legacy id stays re-importable");
    }
}
