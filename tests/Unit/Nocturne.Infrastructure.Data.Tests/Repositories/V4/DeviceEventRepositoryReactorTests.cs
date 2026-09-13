using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers the <see cref="IDeviceEventReactor"/> hook on the device-event write chokepoint, which is what
/// makes a site or sensor change advance its tracker. Placing it at the repository rather than at a
/// controller is the point: connector ingest reaches device events through the decomposer and never
/// touches the V4 REST surface, so these tests pin the seam that both paths share.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "DeviceEvent")]
public class DeviceEventRepositoryReactorTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly RecordingReactor _reactor = new();
    private readonly DeviceEventRepository _repo;

    public DeviceEventRepositoryReactorTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();

        _repo = new DeviceEventRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance,
            broadcaster: null,
            reactor: _reactor);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DeviceEvent NewEvent(DeviceEventType eventType = DeviceEventType.SiteChange) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc),
        EventType = eventType,
    };

    [Fact]
    public async Task LiveCreate_FiresTheReactor()
    {
        var model = NewEvent();

        await _repo.CreateAsync(model, WriteOrigin.Live);

        _reactor.Created.Should().ContainSingle().Which.EventType.Should().Be(DeviceEventType.SiteChange);
    }

    [Fact]
    public async Task BackfillCreate_DoesNotFireTheReactor()
    {
        // A Nightscout migration replays years of site changes; advancing a tracker for each would
        // leave it on whichever one happened to land last.
        await _repo.CreateAsync(NewEvent(), WriteOrigin.Backfill);

        _reactor.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkLiveCreate_FiresTheReactorOnceWithEveryEvent()
    {
        var events = new[]
        {
            NewEvent(),
            NewEvent(DeviceEventType.SensorChange),
        };

        await _repo.BulkCreateAsync(events, WriteOrigin.Live);

        _reactor.Invocations.Should().Be(1);
        _reactor.Created.Should().HaveCount(2);
    }

    [Fact]
    public async Task BulkBackfillCreate_DoesNotFireTheReactor()
    {
        await _repo.BulkCreateAsync([NewEvent(), NewEvent(DeviceEventType.SensorChange)], WriteOrigin.Backfill);

        _reactor.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task ReactorFailure_DoesNotFailTheWrite()
    {
        var repo = new DeviceEventRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance,
            broadcaster: null,
            reactor: new ThrowingReactor());

        var model = NewEvent();

        // The event is already committed by the time the reaction runs, so a tracker that fails to
        // advance must not take the ingest of the underlying record down with it.
        var act = async () => await repo.CreateAsync(model, WriteOrigin.Live);

        await act.Should().NotThrowAsync();
        (await repo.GetByIdAsync(model.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task NoReactorWired_LeavesTheWritePathIntact()
    {
        var repo = new DeviceEventRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance);

        var model = NewEvent();

        await repo.CreateAsync(model, WriteOrigin.Live);

        (await repo.GetByIdAsync(model.Id)).Should().NotBeNull();
    }

    private sealed class RecordingReactor : IDeviceEventReactor
    {
        public List<DeviceEvent> Created { get; } = [];
        public int Invocations { get; private set; }

        public Task OnCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct = default)
        {
            Invocations++;
            Created.AddRange(created);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReactor : IDeviceEventReactor
    {
        public Task OnCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct = default) =>
            throw new InvalidOperationException("tracker repository unavailable");
    }
}
