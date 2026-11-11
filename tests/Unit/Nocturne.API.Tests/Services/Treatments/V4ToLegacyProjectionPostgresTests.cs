using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// <see cref="V4ToLegacyProjectionService.GetProjectedTreatmentsAtAsync"/> on PostgreSQL under the
/// runtime role and Row Level Security, where the per-instant limit and the order of ids are SQL.
/// </summary>
[Trait("Category", "Integration")]
public class V4ToLegacyProjectionPostgresTests(V4ToLegacyProjectionPostgresTests.Database database)
    : IClassFixture<V4ToLegacyProjectionPostgresTests.Database>
{
    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("legacy_projection");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// A crowded instant keeps the records with the newest ids, as the bolus repository's window
    /// read orders them, and takes none of the rows of another instant in the same read.
    /// </summary>
    [Fact]
    public async Task EachInstantIsLimitedOnItsOwnNewestIdFirst()
    {
        var tenant = await SeedTenantAsync();
        var (sparse, crowded) = (At, At.AddDays(1));
        var pileUp = Enumerable.Range(0, 250)
            .Select(i => Bolus(tenant, crowded, Guid.CreateVersion7(new DateTimeOffset(At.AddSeconds(i)))))
            .ToList();
        var alone = Bolus(tenant, sparse, Guid.CreateVersion7());
        await using (var seed = Context(tenant))
        {
            seed.Boluses.AddRange([.. pileUp, alone]);
            await seed.SaveChangesAsync();
        }

        var service = Service(tenant);
        var both = await IdsServedAtAsync(service, sparse, crowded);
        var crowdedAlone = await IdsServedAtAsync(service, crowded);

        crowdedAlone.Should().BeEquivalentTo(pileUp.TakeLast(100).Select(b => b.Id.ToString()));
        both.Should().BeEquivalentTo([.. crowdedAlone, alone.Id.ToString()]);
    }

    private static async Task<List<string?>> IdsServedAtAsync(V4ToLegacyProjectionService service, params DateTime[] instants) =>
        [.. (await service.GetProjectedTreatmentsAtAsync(
            [.. instants.Select(at => new DateTimeOffset(at).ToUnixTimeMilliseconds())])).Select(t => t.Id)];

    private static BolusEntity Bolus(Guid tenant, DateTime at, Guid id) => new()
    {
        Id = id, TenantId = tenant, Timestamp = at, Insulin = 1,
    };

    private V4ToLegacyProjectionService Service(Guid tenant) =>
        new(
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<ITreatmentFoodService>(),
            Context(tenant).AsFactory(),
            NullLogger<V4ToLegacyProjectionService>.Instance);

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'legacy-projection-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"projection-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
