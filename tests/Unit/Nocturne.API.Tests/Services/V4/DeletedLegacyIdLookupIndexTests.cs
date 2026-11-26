using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The tables that carry the deleted-row legacy-id index are listed in Infrastructure, while the
/// isValid:false upload paths that look deleted rows up live here, so only this test ties the two
/// together. A decomposer's lookups are read off the legacy-keyed repositories its constructor takes.
/// </summary>
[Trait("Category", "Unit")]
public class DeletedLegacyIdLookupIndexTests
{
    [Fact]
    public void TheDeletedRowIndexCoversExactlyTheTablesTheDeletedLookupReads()
    {
        var read = DecomposedTreatmentTables.All
            .Select(t => t.GetType().GetGenericArguments()[0])
            .Concat(LegacyKeyedRepositoryEntities(typeof(EntryDecomposer)))
            .Concat(LegacyKeyedRepositoryEntities(typeof(DeviceStatusDecomposer)))
            .ToList();

        read.Should().HaveCount(14, "an empty set would let the equality below pass vacuously");

        var model = Model();
        var needingTheIndex = read.Where(t => !model.FindEntityType(t)!.GetIndexes().Any(i =>
            i.GetFilter() is null
            && i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(ITenantScoped.TenantId), nameof(ILegacyIdentified.LegacyId)])));

        NocturneDbContext.DeletedLegacyIdLookupEntities.Should().BeEquivalentTo(needingTheIndex,
            "every table the deleted lookup reads needs a tenant-led index holding its deleted rows, which one with an unfiltered legacy-id index already has, and no other table does");
    }

    private static IEnumerable<Type> LegacyKeyedRepositoryEntities(Type decomposer) =>
        decomposer.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType.GetInterfaces().Append(p.ParameterType)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ILegacyKeyedRepository<>)))
            .OfType<Type>()
            .Select(keyed => EntityOf(typeof(NocturneDbContext).Assembly.GetTypes()
                .Single(t => t is { IsClass: true, IsAbstract: false } && keyed.IsAssignableFrom(t))));

    private static Type EntityOf(Type repository)
    {
        for (var type = repository; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(V4RepositoryBase<,>))
                return type.GetGenericArguments()[1];
        }

        throw new InvalidOperationException($"{repository.Name} is not a V4 repository");
    }

    private static IModel Model()
    {
        using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .Options);

        return ctx.GetService<IDesignTimeModel>().Model;
    }
}
