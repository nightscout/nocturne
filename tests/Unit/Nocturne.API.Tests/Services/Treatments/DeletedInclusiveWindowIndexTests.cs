using FluentAssertions;
using Nocturne.API.Services.Treatments;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// The tables that carry the unfiltered tenant timestamp index are listed in Infrastructure, while the
/// legacy treatment window reads that take soft-deleted rows live here, so only this test ties the two
/// together.
/// </summary>
[Trait("Category", "Unit")]
public class DeletedInclusiveWindowIndexTests
{
    [Fact]
    public void EveryLegacyTreatmentWindowTable_HasTheUnfilteredTenantTimestampIndex()
    {
        var read = LegacyTreatmentTables.All
            .Select(t => t.GetType())
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(LegacyTreatmentTable<,>))
            .Select(t => t.GetGenericArguments()[1])
            .Append(typeof(ProfileActivationEntity))
            .ToList();

        read.Should().HaveCountGreaterThan(1, "an empty set would let the subset check below pass vacuously");

        read.Should().BeSubsetOf(NocturneDbContext.V4DeletedInclusiveWindowEntities,
            "a legacy treatment window read that takes deleted rows walks the tenant timestamp index, which the partial list indexes cannot stand in for");
    }
}
