using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The tenant guard on <c>SyncUpsertRepositoryBase.SplitUpsertsAsync</c>. The split matches stored
/// rows through <c>IgnoreQueryFilters()</c> — lifting the soft-delete filter also lifts the tenant
/// one — so its explicit <c>TenantId</c> predicate is the only thing keeping one tenant's connector
/// replay out of another tenant's rows. The sync key is unique per tenant, not globally: two tenants
/// uploading from the same connector routinely share a (DataSource, SyncIdentifier) pair, and the
/// partial unique index leads with <c>tenant_id</c> precisely so they can.
/// </summary>
/// <remarks>
/// Deliberately a unit test on SQLite rather than a golden: the integration fixture runs under real
/// Postgres RLS, which masks a missing predicate here. This is the only level at which the guard is
/// observable.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SyncUpsertTenantScopeTests : IDisposable
{
    private const string DataSource = "aaps";
    private const string SyncIdentifier = "sync-1";

    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTime T0 = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly NocturneDbContext _contextA;
    private readonly BolusRepository _repoA;

    public SyncUpsertTenantScopeTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seed = new NocturneDbContext(_options) { TenantId = TenantA })
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new TenantEntity { Id = TenantA, Slug = "tenant-a" });
            seed.Tenants.Add(new TenantEntity { Id = TenantB, Slug = "tenant-b" });
            seed.SaveChanges();
        }

        _contextA = new NocturneDbContext(_options) { TenantId = TenantA };
        _repoA = new BolusRepository(
            new TestTenantDbContextFactory(_contextA),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<BolusRepository>.Instance);
    }

    public void Dispose()
    {
        _contextA.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Persists a bolus on the sync key for <paramref name="tenant"/> and returns its id.</summary>
    private Guid Seed(Guid tenant, double insulin)
    {
        using var ctx = new NocturneDbContext(_options) { TenantId = tenant };
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Timestamp = T0,
            Insulin = insulin,
            DataSource = DataSource,
            SyncIdentifier = SyncIdentifier,
        };
        ctx.Boluses.Add(entity);
        ctx.SaveChanges();
        return entity.Id;
    }

    private async Task<BolusEntity> ReadAsync(Guid id)
    {
        await using var verify = new NocturneDbContext(_options) { TenantId = TenantA };
        return await verify.Boluses.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private async Task<int> CountAsync()
    {
        await using var verify = new NocturneDbContext(_options) { TenantId = TenantA };
        return await verify.Boluses.IgnoreQueryFilters().CountAsync();
    }

    private Task<IEnumerable<Bolus>> UpsertFromTenantAAsync(double insulin) =>
        _repoA.BulkCreateAsync(
            [new Bolus { Timestamp = T0, DataSource = DataSource, SyncIdentifier = SyncIdentifier, Insulin = insulin }],
            WriteOrigin.Live);

    [Fact]
    public async Task BulkCreate_WhenAnotherTenantHoldsTheSameKey_UpsertsOnlyTheCallersRow()
    {
        var rowB = Seed(TenantB, insulin: 1.0);
        var rowA = Seed(TenantA, insulin: 5.0);

        await UpsertFromTenantAAsync(insulin: 9.0);

        (await ReadAsync(rowA)).Insulin.Should().Be(9.0);
        (await ReadAsync(rowB)).Insulin.Should().Be(1.0, "another tenant's row is not the caller's to update");
        (await CountAsync()).Should().Be(2, "the upsert matched in place rather than inserting");
    }

    [Fact]
    public async Task BulkCreate_WhenOnlyAnotherTenantHoldsTheKey_InsertsRatherThanMatchingIt()
    {
        var rowB = Seed(TenantB, insulin: 1.0);

        await UpsertFromTenantAAsync(insulin: 9.0);

        (await ReadAsync(rowB)).Insulin.Should().Be(1.0, "another tenant's row is not a match candidate");
        (await CountAsync()).Should().Be(2, "the caller has no row on this key, so one is inserted");

        await using var verify = new NocturneDbContext(_options) { TenantId = TenantA };
        var inserted = await verify.Boluses.SingleAsync();
        inserted.TenantId.Should().Be(TenantA);
        inserted.Insulin.Should().Be(9.0);
    }
}
