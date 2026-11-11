using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.DeviceStatuses;

/// <summary>
/// Delete, bulk delete and replace find the stored status by the id it is read under, which a path's
/// wire form may only resolve to, and count statuses, not the records each is stored as.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusServiceWriteTests
{
    private const string StoredId = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<IDeviceStatusGroupRepository> _groups = new();
    private readonly Mock<IApsSnapshotRepository> _aps = new();
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<IDataEventSink<DeviceStatus>> _events = new();

    [Fact]
    public async Task Delete_ResolvesAWireIdToTheStoredId_AndCountsTheStatusOnce()
    {
        var uuid = Guid.CreateVersion7();
        Assert.True(MongoObjectId.TryGetGuidPrefixRange(MongoObjectId.FromGuid(uuid), out var low, out var high));
        _aps.Setup(r => r.GetByGuidRangeAsync(low, high, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApsSnapshot { Id = uuid, Timestamp = DateTime.UtcNow });
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DeleteStoredAsync(uuid.ToString(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var deleted = await Service(decomposer.Object).DeleteDeviceStatusAsync(MongoObjectId.FromGuid(uuid));

        deleted.Should().BeTrue();
        _sideEffects.Verify(s => s.OnDeletedAsync(
            "devicestatus", It.Is<DeviceStatus>(d => d.Id == uuid.ToString()), It.IsAny<WriteEffectOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnDeletedAsync(It.Is<DeviceStatus>(d => d.Id == uuid.ToString()), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_AnUnknownId_DeletesNothing()
    {
        var deleted = await Service(new Mock<IDeviceStatusDecomposer>(MockBehavior.Strict).Object)
            .DeleteDeviceStatusAsync(StoredId);

        deleted.Should().BeFalse();
        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BulkDelete_AnIdFind_DeletesThatStatusOnce()
    {
        _aps.Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApsSnapshot { Id = Guid.CreateVersion7(), LegacyId = StoredId, Timestamp = DateTime.UtcNow });
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer.Setup(d => d.DeleteStoredAsync(StoredId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var deleted = await Service(decomposer.Object)
            .DeleteDeviceStatusesAsync(FindQuery.ParseDeviceStatus($"find[_id]={StoredId}"));

        deleted.Should().Be(1);
        decomposer.Verify(
            d => d.DeleteStoredAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _sideEffects.Verify(s => s.OnBulkDeletedAsync("devicestatus", 1, It.IsAny<WriteEffectOptions>(), It.IsAny<CancellationToken>()));
        _events.Verify(e => e.OnBulkDeletedAsync(1, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task BulkDelete_MatchingNothing_StillAnnouncesTheBulkDeleteOfNone()
    {
        _groups
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var deleted = await Service(new Mock<IDeviceStatusDecomposer>(MockBehavior.Strict).Object)
            .DeleteDeviceStatusesAsync(FindQuery.ParseDeviceStatus("find[device]=openaps://rig"));

        deleted.Should().Be(0);
        _sideEffects.Verify(s => s.OnBulkDeletedAsync("devicestatus", 0, It.IsAny<WriteEffectOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnBulkDeletedAsync(0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Replace_AnUnknownId_ReplacesNothing()
    {
        var replaced = await Service(new Mock<IDeviceStatusDecomposer>(MockBehavior.Strict).Object)
            .ReplaceDeviceStatusAsync(StoredId, new DeviceStatus { Device = "loop://iPhone" });

        replaced.Should().BeNull();
        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replace_ReadsTheStatusBackUnderTheIdItWasWrittenUnder_ElseServesTheBody(bool readsBack)
    {
        const string writtenId = "5f8d0c1e8a7b4c3d9e5f0002";
        _aps.Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApsSnapshot { Id = Guid.CreateVersion7(), LegacyId = StoredId, Timestamp = DateTime.UtcNow });
        if (readsBack)
            _aps.Setup(r => r.GetByLegacyIdAsync(writtenId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApsSnapshot
                {
                    Id = Guid.CreateVersion7(), LegacyId = writtenId, Timestamp = DateTime.UtcNow, Device = "served",
                });
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.ReplaceAsync(StoredId, It.IsAny<DeviceStatus>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .Callback<string, DeviceStatus, WriteOrigin, CancellationToken>((_, body, _, _) => body.Id = writtenId)
            .ReturnsAsync(new DecompositionResult());
        var body = new DeviceStatus { Device = "body" };

        var replaced = await Service(decomposer.Object).ReplaceDeviceStatusAsync(StoredId, body);

        replaced!.Id.Should().Be(writtenId);
        replaced.Device.Should().Be(readsBack ? "served" : "body");
        _sideEffects.Verify(s => s.OnUpdatedAsync("devicestatus", replaced, It.IsAny<WriteEffectOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnUpdatedAsync(replaced, It.IsAny<CancellationToken>()), Times.Once);
    }

    private DeviceStatusService Service(IDeviceStatusDecomposer decomposer)
    {
        var stateSpans = new Mock<IStateSpanRepository>();
        stateSpans
            .Setup(r => r.GetByCategory(It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        return new DeviceStatusService(
            new DeviceStatusProjectionService(
                _groups.Object,
                _aps.Object,
                Mock.Of<IPumpSnapshotRepository>(),
                Mock.Of<IUploaderSnapshotRepository>(),
                stateSpans.Object,
                Mock.Of<IDeviceStatusExtrasRepository>(),
                TimeProvider.System,
                NullLogger<DeviceStatusProjectionService>.Instance),
            decomposer,
            _sideEffects.Object,
            _events.Object,
            NullLogger<DeviceStatusService>.Instance);
    }
}
