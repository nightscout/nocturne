using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The arrival a reading's glucose facts are aged from (docs/alerts/engine-semantics.md §3) is the
/// time its row was stored, which only an insert sets.
/// </summary>
[Trait("Category", "Unit")]
public class ReadingArrivalTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _repository;

    public ReadingArrivalTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _repository = new SensorGlucoseRepository(
            new TestNocturneDbContextFactory(_context),
            Mock.Of<IDuplicateLinker>(),
            Mock.Of<IAuditContext>(),
            NullLogger<SensorGlucoseRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SensorGlucose Reading(string key, DateTime at, double mgdl) => new()
    {
        Timestamp = at,
        Mgdl = mgdl,
        DataSource = "cgm-app",
        SyncIdentifier = key,
    };

    private async Task<SensorGlucose> NewestAsync() =>
        (await _repository.GetAsync(null, null, null, null, limit: 1)).Single();

    [Fact]
    public async Task A_reupload_or_edit_of_a_reading_keeps_its_arrival()
    {
        await _repository.UpsertAsync(Reading("r1", T0, 100), WriteOrigin.Live);
        var first = await NewestAsync();
        await Task.Delay(20);

        await _repository.UpsertAsync(Reading("r1", T0, 104), WriteOrigin.Live);
        var again = await NewestAsync();

        again.Id.Should().Be(first.Id);
        again.Mgdl.Should().Be(104);
        again.CreatedAt.Should().Be(first.CreatedAt);
        CanonicalAlertEvaluator.ContextFor(again, default).LatestReceivedAt.Should().Be(first.CreatedAt);
    }

    [Fact]
    public void The_recorded_next_change_is_keyed_by_the_arrival_too()
    {
        var reading = new SensorGlucose { Timestamp = T0, CreatedAt = T0, Mgdl = 100 };
        var onTime = CanonicalAlertEvaluator.ContextFor(reading, default);
        var late = onTime with { LatestReceivedAt = T0.AddMinutes(20) };

        NextChange.GlucoseOf(late).Should().NotBe(NextChange.GlucoseOf(onTime));
    }

    [Fact]
    public async Task An_older_reading_that_arrives_later_leaves_the_newest_and_its_arrival()
    {
        await _repository.UpsertAsync(Reading("r2", T0.AddMinutes(10), 100), WriteOrigin.Live);
        var newest = await NewestAsync();
        await Task.Delay(20);

        await _repository.UpsertAsync(Reading("r1", T0.AddMinutes(5), 90), WriteOrigin.Live);

        var after = await NewestAsync();
        after.Id.Should().Be(newest.Id);
        after.CreatedAt.Should().Be(newest.CreatedAt);
    }

    /// <summary>A stored row with its arrival and writer set as an insert would have set them.</summary>
    private async Task<Guid> StoredAsync(
        DateTime at, DateTime arrival, string? source = null, string? device = null, Guid? writer = null)
    {
        var id = Guid.NewGuid();
        _context.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = id, TenantId = TenantId, Timestamp = at, Mgdl = 100, DataSource = source, Device = device,
        });
        await _context.SaveChangesAsync();
        await _context.SensorGlucose.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.SysCreatedAt, arrival)
            .SetProperty(e => e.WriterGrantId, writer));
        return id;
    }

    private static OAuthGrantRecord AppGrant(Guid client) => new(
        Guid.NewGuid(), TenantId, client, new OAuthGrantClient("app", "App", null, null),
        OAuthClientIdentity.SelfRegistered, Guid.NewGuid(), null, OAuthGrantTypes.App, [], null,
        T0.AddDays(-10), null, null, null, null, null, null, null, null, null, false, false);

    /// <summary>
    /// Imported history is what is dated before its connection first sent anything
    /// (engine-semantics.md §8). A connection is folded as the connections list folds it: an app's
    /// grants are one connection, so the backlog an app uploads after it is authorised again, under
    /// a new grant and on a new phone, is a late arrival; another app is another connection; a reading
    /// no credential wrote belongs to its data source; a reading of no connection is compared with the
    /// tenant's first arrival. A reading uploaded late into a connection that was already sending is
    /// not imported, however late.
    /// </summary>
    [Fact]
    public async Task Imported_history_is_dated_before_its_connections_first_arrival()
    {
        var client = Guid.NewGuid();
        var (first, again, other) = (AppGrant(client), AppGrant(client), AppGrant(Guid.NewGuid()));
        var grants = new Mock<IOAuthGrantRepository>();
        grants.Setup(g => g.ListForConnectionsAsync(null, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([first, again, other]);

        await StoredAsync(T0, T0, source: "xdrip", device: "phone-1", writer: first.Id);
        var backlog = await StoredAsync(T0.AddMinutes(35), T0.AddMinutes(52), source: "xdrip", device: "phone-2", writer: again.Id);
        var resent = await StoredAsync(T0.AddHours(1), T0.AddDays(30), source: "xdrip", writer: first.Id);
        var otherApp = await StoredAsync(T0.AddMinutes(40), T0.AddHours(2), source: "xdrip", writer: other.Id);
        await StoredAsync(T0.AddHours(3), T0.AddHours(3), source: "xdrip", writer: other.Id);
        await StoredAsync(T0, T0, source: "dexcom");
        var backfilled = await StoredAsync(T0.AddDays(-1), T0.AddHours(1), source: "dexcom");
        var noConnection = await StoredAsync(T0.AddHours(-2), T0.AddHours(3));
        await StoredAsync(T0.AddMinutes(5), T0.AddHours(3));
        var outside = await StoredAsync(T0.AddDays(-3), T0.AddHours(1), source: "dexcom");

        var imported = await new ImportedHistory(_repository, grants.Object, TestTenantCache.Create(tenantId: TenantId))
            .InAsync(T0.AddDays(-2), T0.AddDays(1), CancellationToken.None);

        imported.Should().BeEquivalentTo([otherApp, backfilled, noConnection]);
        imported.Should().NotContain([backlog, resent], "a connection's backlog is a late arrival, however late");
        imported.Should().NotContain(outside, "only the window's readings are classified");
    }
}
