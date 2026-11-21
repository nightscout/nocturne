using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
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
}
