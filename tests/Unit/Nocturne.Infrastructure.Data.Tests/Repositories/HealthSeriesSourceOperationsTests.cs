using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.Health;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The operations a connector runs over everything one source wrote: each stays inside that source
/// and the context's tenant.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class HealthSeriesSourceOperationsTests : IDisposable
{
    private const string Source = "google-health-connector";
    private const string OtherSource = "manual";

    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public HealthSeriesSourceOperationsTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "a").SeedTenant(_otherTenant, "b");
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private HeartRateRepository HeartRates(Guid tenant) =>
        new(_db.CreateContext(tenant).AsFactory(), TimeProvider.System);

    private BodyWeightRepository BodyWeights(Guid tenant) =>
        new(_db.CreateContext(tenant).AsFactory(), TimeProvider.System);

    private Guid AddHeartRate(Guid tenant, string source, string? syncIdentifier, DateTime at,
        DateTime? deletedAt = null, bool deletedByUser = false)
    {
        using var db = _db.CreateContext(tenant);
        var row = new HeartRateEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, Bpm = 60,
            DataSource = source, SyncIdentifier = syncIdentifier, DeletedAt = deletedAt,
        };
        db.HeartRates.Add(row);
        db.Entry(row).Property<bool>(NocturneDbContext.DeletedByUserProperty).CurrentValue = deletedByUser;
        db.SaveChanges();
        return row.Id;
    }

    private Guid AddBodyWeight(Guid tenant, string source, string syncIdentifier, DateTime at)
    {
        using var db = _db.CreateContext(tenant);
        var row = new BodyWeightEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Mills = at.ToMills(), WeightKg = 70,
            DataSource = source, SyncIdentifier = syncIdentifier,
        };
        db.BodyWeights.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private List<(string? Source, string? Key, bool Deleted, bool ByUser)> HeartRateRows(Guid tenant)
    {
        using var db = _db.CreateContext(tenant);
        return db.HeartRates.IncludingDeleted().AsNoTracking()
            .Select(r => new { r.DataSource, r.SyncIdentifier, r.DeletedAt, ByUser = EF.Property<bool>(r, NocturneDbContext.DeletedByUserProperty) })
            .AsEnumerable()
            .Select(r => (r.DataSource, r.SyncIdentifier, r.DeletedAt != null, r.ByUser))
            .ToList();
    }

    [Fact]
    public async Task PurgeSourceAsync_DeletesTheSourcesRowsAndTombstonesOfTheTenantOnly()
    {
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            AddHeartRate(tenant, Source, "live", From);
            AddHeartRate(tenant, Source, "swept", From, deletedAt: From);
            AddHeartRate(tenant, Source, "user-deleted", From, deletedAt: From, deletedByUser: true);
            AddHeartRate(tenant, OtherSource, "live", From);
        }

        var purged = await HeartRates(_tenant).PurgeSourceAsync(Source);

        purged.Should().Be(3);
        HeartRateRows(_tenant).Should().ContainSingle().Which.Source.Should().Be(OtherSource);
        HeartRateRows(_otherTenant).Should().HaveCount(4);
    }

    [Fact]
    public async Task GetSyncKeysAsync_ReadsTheSourcesLiveKeyedRowsObservedInTheWindow()
    {
        var inWindow = AddHeartRate(_tenant, Source, "in-window", From.AddHours(6));
        AddHeartRate(_tenant, Source, "at-end", To);
        AddHeartRate(_tenant, Source, "before", From.AddTicks(-1));
        AddHeartRate(_tenant, Source, null, From.AddHours(6));
        AddHeartRate(_tenant, Source, "swept", From.AddHours(6), deletedAt: From);
        AddHeartRate(_tenant, OtherSource, "other-source", From.AddHours(6));
        AddHeartRate(_otherTenant, Source, "other-tenant", From.AddHours(6));

        var keys = await HeartRates(_tenant).GetSyncKeysAsync(Source, From, To, limit: 10);

        keys.Should().Equal(new SourceRecordKey(inWindow, "in-window"));
    }

    [Fact]
    public async Task GetSyncKeysAsync_ReadsBodyWeightsByTheirMilliseconds()
    {
        var inWindow = AddBodyWeight(_tenant, Source, "in-window", From);
        AddBodyWeight(_tenant, Source, "at-end", To);
        AddBodyWeight(_tenant, OtherSource, "other-source", From);
        AddBodyWeight(_otherTenant, Source, "other-tenant", From);

        var keys = await BodyWeights(_tenant).GetSyncKeysAsync(Source, From, To, limit: 10);

        keys.Should().Equal(new SourceRecordKey(inWindow, "in-window"));
    }

    [Fact]
    public async Task GetSyncKeysAsync_ReadsNoMoreThanTheLimit()
    {
        for (var i = 0; i < 3; i++)
            AddHeartRate(_tenant, Source, $"key-{i}", From.AddMinutes(i));

        (await HeartRates(_tenant).GetSyncKeysAsync(Source, From, To, limit: 2)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RetireAsync_SoftDeletesTheSourcesListedRowsAsASystemDelete()
    {
        var stale = AddHeartRate(_tenant, Source, "stale", From);
        AddHeartRate(_tenant, Source, "kept", From);
        var otherSource = AddHeartRate(_tenant, OtherSource, "other-source", From);
        var otherTenant = AddHeartRate(_otherTenant, Source, "other-tenant", From);

        var retired = await HeartRates(_tenant).RetireAsync(Source, [stale, otherSource, otherTenant], From);

        retired.Should().Be(1);
        HeartRateRows(_tenant).Should().BeEquivalentTo(new[]
        {
            ((string?)Source, (string?)"stale", true, false),
            (Source, "kept", false, false),
            (OtherSource, "other-source", false, false),
        });
        HeartRateRows(_otherTenant).Should().ContainSingle().Which.Deleted.Should().BeFalse();
    }
}
