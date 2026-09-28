using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.API.Services.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The tables that carry the <see cref="HistoryPage"/> index are listed in Infrastructure, while the
/// tables the history endpoints page live here, so only the model ties the two together.
/// </summary>
[Trait("Category", "Unit")]
public class HistoryPagedIndexTests
{
    [Fact]
    public void TheHistoryIndexCoversExactlyTheTablesHistoryPages()
    {
        var paged = LegacyTreatmentTables.All
            .Select(t => t.GetType().GetGenericArguments()[1])
            .Append(typeof(ApsSnapshotEntity))
            .ToList();

        paged.Should().HaveCount(8, "an empty set would let the equality below pass vacuously");

        var indexed = Model().GetEntityTypes()
            .Where(e => e.GetIndexes().Any(IsHistoryIndex))
            .Select(e => e.ClrType)
            .ToList();

        indexed.Should().BeEquivalentTo(paged,
            "every table a history endpoint pages on sys_updated_at needs the index, and no other table does");
    }

    private static bool IsHistoryIndex(IIndex index) =>
        index.GetDatabaseName() == $"ix_{index.DeclaringEntityType.GetTableName()}_tenant_sys_updated_at"
        && index.Properties.Select(p => p.Name).SequenceEqual([
            nameof(ITenantScoped.TenantId),
            nameof(ISystemTimestamped.SysUpdatedAt),
            nameof(IIdentified.Id)])
        && !index.IsUnique
        && !(index.IsDescending ?? []).Contains(true)
        && index.GetFilter() == "deleted_at IS NULL";

    /// <summary>
    /// Index sort order lives only on the design-time model. Building it opens no connection.
    /// </summary>
    private static IModel Model()
    {
        using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .Options);

        return ctx.GetService<IDesignTimeModel>().Model;
    }
}
