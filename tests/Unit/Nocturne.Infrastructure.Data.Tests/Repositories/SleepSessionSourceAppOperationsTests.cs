using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The operations a connector runs over every session one app recorded through one source: each
/// stays inside that source, that app and the context's tenant.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SleepSessionSourceAppOperationsTests : IDisposable
{
    private const string App = "Google Health";

    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public SleepSessionSourceAppOperationsTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "a").SeedTenant(_otherTenant, "b");
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private SleepSessionRepository Repository(Guid tenant) => new(_db.CreateContext(tenant).AsFactory());

    private Guid AddSession(Guid tenant, string originalId, DateTime end, string source = "Google", string app = App,
        DateTime? deletedAt = null, bool deletedByUser = false)
    {
        using var db = _db.CreateContext(tenant);
        var session = new SleepSessionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, StartTime = end.AddHours(-8), EndTime = end,
            Source = source, SourceApp = app, OriginalId = originalId, DeletedAt = deletedAt,
        };
        db.SleepSessions.Add(session);
        db.Entry(session).Property<bool>(NocturneDbContext.DeletedByUserProperty).CurrentValue = deletedByUser;
        db.SleepStages.Add(new SleepStageEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, SleepSessionId = session.Id,
            StartTime = session.StartTime, EndTime = end, Stage = "Light",
        });
        db.SaveChanges();
        return session.Id;
    }

    /// <summary>A session of every neighbour the operations must leave alone, in each tenant.</summary>
    private List<Guid> AddNeighbours()
    {
        var ids = new List<Guid>();
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            ids.Add(AddSession(tenant, "other-source", From.AddHours(8), source: "Manual"));
            ids.Add(AddSession(tenant, "other-app", From.AddHours(8), app: "Google Fit"));
        }
        ids.Add(AddSession(_otherTenant, "other-tenant", From.AddHours(8)));
        return ids;
    }

    private List<(string? Key, bool Deleted, bool ByUser)> Sessions(Guid tenant)
    {
        using var db = _db.CreateContext(tenant);
        return db.SleepSessions.IncludingDeleted().AsNoTracking()
            .Select(s => new { s.OriginalId, s.DeletedAt, ByUser = EF.Property<bool>(s, NocturneDbContext.DeletedByUserProperty) })
            .AsEnumerable()
            .Select(s => (s.OriginalId, s.DeletedAt != null, s.ByUser))
            .ToList();
    }

    private int Stages(Guid tenant)
    {
        using var db = _db.CreateContext(tenant);
        return db.SleepStages.IgnoreQueryFilters().Count(s => s.TenantId == tenant);
    }

    [Fact]
    public async Task PurgeSourceAppAsync_DeletesTheAppsSessionsAndTombstonesWithTheirStages()
    {
        AddNeighbours();
        AddSession(_tenant, "live", From);
        AddSession(_tenant, "swept", From, deletedAt: From);
        AddSession(_tenant, "user-deleted", From, deletedAt: From, deletedByUser: true);

        var purged = await Repository(_tenant).PurgeSourceAppAsync(SleepSource.Google, App);

        purged.Should().Be(3);
        Sessions(_tenant).Select(s => s.Key).Should().BeEquivalentTo("other-source", "other-app");
        Stages(_tenant).Should().Be(2);
        Sessions(_otherTenant).Should().HaveCount(3);
    }

    [Fact]
    public async Task GetSourceAppKeysAsync_ReadsTheAppsLiveSessionsThatEndInTheWindow()
    {
        AddNeighbours();
        var inWindow = AddSession(_tenant, "in-window", From.AddHours(8));
        AddSession(_tenant, "at-end", To);
        AddSession(_tenant, "swept", From.AddHours(8), deletedAt: From);

        var keys = await Repository(_tenant).GetSourceAppKeysAsync(SleepSource.Google, App, From, To, limit: 10);

        keys.Should().Equal(new SourceRecordKey(inWindow, "in-window"));
    }

    [Fact]
    public async Task RetireAsync_SoftDeletesTheAppsListedSessionsAsASystemDelete()
    {
        var neighbours = AddNeighbours();
        var stale = AddSession(_tenant, "stale", From);
        AddSession(_tenant, "kept", From);

        var retired = await Repository(_tenant).RetireAsync(SleepSource.Google, App, [stale, .. neighbours], From);

        retired.Should().Be(1);
        Sessions(_tenant).Should().ContainSingle(s => s.Deleted).Which.Should().Be(("stale", true, false));
        Sessions(_otherTenant).Should().OnlyContain(s => !s.Deleted);
    }

    [Fact]
    public async Task DescribeSourceAppAsync_CountsTheAppsLiveSessions()
    {
        AddNeighbours();
        AddSession(_tenant, "older", From);
        AddSession(_tenant, "newer", To);
        AddSession(_tenant, "swept", To, deletedAt: From);

        var stats = await Repository(_tenant).DescribeSourceAppAsync(SleepSource.Google, App, null, To.AddHours(-9));

        stats.Should().Be(new SleepSessionStats(2, 1, To.AddHours(-8), From.AddHours(-8)));
        (await Repository(_tenant).DescribeSourceAppAsync(SleepSource.Google, App, To, To)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAndAllowReimport_ReleaseOnlyTheAppsUserDeletions()
    {
        AddNeighbours();
        AddSession(_tenant, "live", From);
        AddSession(_otherTenant, "other-tenant-tombstone", From, deletedAt: From, deletedByUser: true);
        var owner = new Mock<IAuditContext>();
        owner.SetupGet(a => a.SubjectId).Returns(Guid.CreateVersion7());
        var scope = "data_source=google-health-connector";

        var deleted = await Repository(_tenant).DeleteSourceAppAsync(SleepSource.Google, App, owner.Object, scope);

        deleted.Should().Be(1);
        Sessions(_tenant).Should().ContainSingle(s => s.Deleted).Which.Should().Be(("live", true, true));

        var released = await Repository(_tenant).AllowSourceAppReimportAsync(SleepSource.Google, App, owner.Object, scope);

        released.Should().Be(1);
        Sessions(_tenant).Should().ContainSingle(s => s.Deleted).Which.Should().Be(("live", true, false));
        Sessions(_otherTenant).Should().Contain(("other-tenant-tombstone", true, true));
    }
}
