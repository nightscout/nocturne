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

    private static string LegacyIdOfShape(string shape, Guid uuid) => shape switch
    {
        "uppercase uuid" => uuid.ToString().ToUpperInvariant(),
        "lowercase uuid" => uuid.ToString(),
        "dashless uuid" => uuid.ToString("N"),
        "hex sync identifier" => Convert.ToHexStringLower(uuid.ToByteArray()) + "0a1b",
        _ => "syn-" + Convert.ToHexStringLower(uuid.ToByteArray()),
    };

    private async Task<(ICarbIntakeRepository Repo, CarbIntake Created, IServiceScope Scope)> StoreCarbAsync(string legacyId)
    {
        var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();
        var created = await repo.CreateAsync(
            new CarbIntake { Timestamp = T0, Carbs = 20, DataSource = "loop", LegacyId = legacyId },
            WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(
            new CarbIntake { Timestamp = T0, Carbs = 30, DataSource = "loop", LegacyId = Guid.NewGuid().ToString() },
            WriteOrigin.Live, CancellationToken.None);
        return (repo, created, scope);
    }

    [Theory]
    [InlineData("uppercase uuid")]
    [InlineData("lowercase uuid")]
    [InlineData("dashless uuid")]
    public async Task GetByLegacyIdUuidPrefix_ResolvesTheIdCoerceEchoedForAUuidLegacyId(string shape)
    {
        var legacyId = LegacyIdOfShape(shape, Guid.NewGuid());
        var (repo, created, scope) = await StoreCarbAsync(legacyId);
        using var tenantScope = scope;

        var resolved = await repo.GetByLegacyIdUuidPrefixAsync(MongoObjectId.Coerce(legacyId)!, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(created.Id);
    }

    [Theory]
    [InlineData("hex sync identifier")]
    [InlineData("synthetic id")]
    public async Task GetByLegacyIdHash_ResolvesTheIdCoerceEchoedForAnyOtherLegacyId(string shape)
    {
        var legacyId = LegacyIdOfShape(shape, Guid.NewGuid());
        var (repo, created, scope) = await StoreCarbAsync(legacyId);
        using var tenantScope = scope;

        var resolved = await repo.GetByLegacyIdHashAsync(MongoObjectId.Coerce(legacyId)!, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetByLegacyIdUuidPrefix_DoesNotMatchANonUuidLegacyIdSharingThePrefix()
    {
        var hex = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "0a1b";
        var (repo, _, scope) = await StoreCarbAsync(hex);
        using var tenantScope = scope;

        var resolved = await repo.GetByLegacyIdUuidPrefixAsync(hex[..24], CancellationToken.None);

        resolved.Should().BeNull("Coerce hashes a 36-hex id rather than taking its prefix");
    }

    [Fact]
    public async Task CoercedLegacyIdLookups_DoNotReachAnotherTenantsRecord()
    {
        var uuidLegacyId = Guid.NewGuid().ToString().ToUpperInvariant();
        var hashedLegacyId = "syn-" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        using (var owner = await _fx.BeginTenantScopeAsync(Guid.NewGuid()))
        {
            var ownerRepo = owner.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();
            foreach (var legacyId in new[] { uuidLegacyId, hashedLegacyId })
                await ownerRepo.CreateAsync(
                    new CarbIntake { Timestamp = T0, Carbs = 20, DataSource = "loop", LegacyId = legacyId },
                    WriteOrigin.Live, CancellationToken.None);
        }

        using var other = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = other.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();

        (await repo.GetByLegacyIdUuidPrefixAsync(MongoObjectId.Coerce(uuidLegacyId)!, CancellationToken.None)).Should().BeNull();
        (await repo.GetByLegacyIdHashAsync(MongoObjectId.Coerce(hashedLegacyId)!, CancellationToken.None)).Should().BeNull();
    }
}
