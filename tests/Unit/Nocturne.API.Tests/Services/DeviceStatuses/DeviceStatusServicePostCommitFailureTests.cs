using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.DeviceStatuses;

/// <summary>
/// A bulk write that commits and then fails in a step after its commit still accounts for the rows it
/// committed, so the statuses they wrote are announced.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DeviceStatusServicePostCommitFailureTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string First = "65f0a1b2c3d4e5f601234571";
    private const string Second = "65f0a1b2c3d4e5f601234572";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<IDataEventSink<DeviceStatus>> _events = new();

    public DeviceStatusServicePostCommitFailureTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task ABroadcastCancelledAfterTheCommit_StillAnnouncesTheCommittedStatuses()
    {
        var broadcaster = new Mock<IRecordChangePublisher>();
        broadcaster
            .Setup(b => b.PublishAsync(
                It.Is<RecordsChanged>(batch => batch.Changes.Any(c =>
                    c.Origin == WriteOrigin.Live && c.RecordType == typeof(UploaderSnapshot) && c.Created.Count > 0)),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("the client went away"));
        var service = Service(Decomposer(uploaderBroadcaster: broadcaster.Object, spans: Mock.Of<IStateSpanService>()));

        var create = () => service.CreateDeviceStatusesAsync([Uploaded(First, 80), Uploaded(Second, 60)]);

        await create.Should().ThrowAsync<OperationCanceledException>();
        _context.UploaderSnapshots.Should().HaveCount(2, "the write committed before the broadcast failed");
        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Select(s => s.Id).SequenceEqual(new[] { First, Second })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A re-send whose only write repairs a lost span is announced even when the span write is cancelled
    /// after its commit.
    /// </summary>
    [Fact]
    public async Task ASpanRepairCancelledAfterTheCommit_StillAnnouncesTheStatus()
    {
        var cancelled = false;
        var legacyReads = new Mock<ILegacyReadCacheInvalidation>();
        legacyReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Returns(() => cancelled ? Task.FromException(new OperationCanceledException("the client went away")) : Task.CompletedTask);
        var spans = new StateSpanService(
            new StateSpanRepository(_context.AsFactory(), Mock.Of<IDuplicateLinker>(), new SystemAuditContext(),
                NullLogger<StateSpanRepository>.Instance, legacyReads.Object),
            NullLogger<StateSpanService>.Instance);
        var service = Service(Decomposer(uploaderBroadcaster: null, spans: spans));
        var suspended = new DeviceStatus
        {
            Id = First, Mills = 1_790_000_000_000, Device = "phone",
            Pump = new PumpStatus { Manufacturer = "Insulet", Model = "Omnipod", Status = new PumpStatusDetails { Suspended = true } },
        };
        await service.CreateDeviceStatusesAsync([suspended]);
        await _context.StateSpans.ExecuteDeleteAsync();
        _events.Invocations.Clear();
        cancelled = true;

        var resend = () => service.CreateDeviceStatusesAsync([suspended]);

        await resend.Should().ThrowAsync<OperationCanceledException>();
        _context.StateSpans.Should().ContainSingle("the repair committed before the cancellation");
        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Single().Id == First), It.IsAny<CancellationToken>()), Times.Once);
    }

    private DeviceStatusDecomposer Decomposer(IRecordChangePublisher? uploaderBroadcaster, IStateSpanService spans) =>
        DeviceStatusDecomposerDoubles.Stored(
            _context.AsFactory(), new SystemAuditContext(), spans, uploaderBroadcaster);

    private DeviceStatusService Service(DeviceStatusDecomposer decomposer) =>
        new(
            new DeviceStatusProjectionService(
                Mock.Of<IDeviceStatusGroupRepository>(),
                Mock.Of<IApsSnapshotRepository>(),
                Mock.Of<IPumpSnapshotRepository>(),
                Mock.Of<IUploaderSnapshotRepository>(),
                Mock.Of<IStateSpanRepository>(),
                Mock.Of<IDeviceStatusExtrasRepository>(),
                TimeProvider.System,
                NullLogger<DeviceStatusProjectionService>.Instance),
            decomposer,
            _sideEffects.Object,
            _events.Object,
            NullLogger<DeviceStatusService>.Instance);

    private static DeviceStatus Uploaded(string id, int battery) => new()
    {
        Id = id,
        Mills = 1_790_000_000_000,
        Device = "phone",
        UploaderBattery = battery,
    };
}
