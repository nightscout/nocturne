using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// The AAPS ObjectId round-trip (issue #522) rests on one Postgres-specific fact: a record's UUID
/// can be resolved from the first 24 hex chars of its canonical form via a uuid prefix range,
/// because Postgres orders <c>uuid</c> byte-wise (= hex-string order). .NET's own Guid ordering is
/// different, so this can only be verified against a real Postgres container, not the InMemory
/// provider. These goldens pin that equivalence for the on-base <c>GetByGuidRangeAsync</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class ObjectIdRangeResolutionGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public ObjectIdRangeResolutionGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetByGuidRange_ResolvesObjectIdDerivedFromUuid()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        var created = await repo.CreateAsync(
            new Bolus { Timestamp = T0, Insulin = 2.5, DataSource = "aaps", LegacyId = "b-range-1" },
            WriteOrigin.Live, CancellationToken.None);

        // The 24-hex ObjectId AAPS sees on the wire, derived from the record's UUID.
        var objectId = MongoObjectId.FromGuid(created.Id);
        MongoObjectId.TryGetGuidPrefixRange(objectId, out var low, out var high).Should().BeTrue();

        var resolved = await repo.GetByGuidRangeAsync(low, high, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(created.Id, "Postgres uuid ordering must select the source record by its hex prefix");
    }

    [Fact]
    public async Task GetByGuidRange_DoesNotResolveUnrelatedPrefix()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        await repo.CreateAsync(
            new Bolus { Timestamp = T0, Insulin = 1.0, DataSource = "aaps", LegacyId = "b-range-2" },
            WriteOrigin.Live, CancellationToken.None);

        // A far-future prefix a UUID v7 (2026-era timestamp) can never share.
        MongoObjectId.TryGetGuidPrefixRange("ffffffffffffffffffffffff", out var low, out var high).Should().BeTrue();

        var resolved = await repo.GetByGuidRangeAsync(low, high, CancellationToken.None);

        resolved.Should().BeNull();
    }
}
