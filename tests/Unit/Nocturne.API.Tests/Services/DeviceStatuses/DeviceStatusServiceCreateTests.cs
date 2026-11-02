using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.DeviceStatuses;

/// <summary>
/// A live create, an upload or a connector's, is announced as reads serve it, and only a status that wrote
/// a record; a backfill is written without an announcement.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusServiceCreateTests
{
    private const string Id = "65f0a1b2c3d4e5f601234567";
    private const string OtherId = "65f0a1b2c3d4e5f601234568";
    private static readonly DateTime At = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IDeviceStatusDecomposer> _decomposer = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaders = new();
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<IDataEventSink<DeviceStatus>> _events = new();
    private readonly DeviceStatusService _service;

    public DeviceStatusServiceCreateTests()
    {
        SetupDecomposed();
        _uploaders
            .Setup(r => r.GetByLegacyIdAsync(Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploaderSnapshot { Id = Guid.CreateVersion7(), LegacyId = Id, Timestamp = At, Battery = 64 });
        _uploaders
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _service = new DeviceStatusService(
            new DeviceStatusProjectionService(
                Mock.Of<IDeviceStatusGroupRepository>(),
                Mock.Of<IApsSnapshotRepository>(),
                Mock.Of<IPumpSnapshotRepository>(),
                _uploaders.Object,
                Mock.Of<IStateSpanRepository>(),
                Mock.Of<IDeviceStatusExtrasRepository>(),
                TimeProvider.System,
                NullLogger<DeviceStatusProjectionService>.Instance),
            _decomposer.Object,
            _sideEffects.Object,
            _events.Object,
            NullLogger<DeviceStatusService>.Instance);
    }

    [Fact]
    public async Task AnUploadIsAnnouncedAsReadsServeIt()
    {
        var created = await _service.CreateDeviceStatusesAsync([Posted(Id)]);

        var served = created.Should().ContainSingle().Subject;
        served.Uploader!.Battery.Should().Be(64);
        served.DataSource.Should().BeNull();
        VerifyAnnounced(served);
        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Count == 1), null, WriteOrigin.Live, It.IsAny<CancellationToken>(), It.IsAny<DeviceStatusDecomposition?>()), Times.Once);
    }

    /// <summary>A status the user deleted writes nothing, so nothing is announced for it.</summary>
    [Fact]
    public async Task AStatusThatWroteNothingIsNotAnnounced()
    {
        SetupDecomposed([true, false], skippedDeleted: 1);

        var created = await _service.CreateDeviceStatusesAsync([Posted(Id), Posted(OtherId)]);

        created.Select(s => s.Id).Should().Equal(Id, OtherId);
        created.SkippedDeleted.Should().Be(1);
        VerifyAnnounced(created[0]);
    }

    /// <summary>A re-send whose every record is already stored as it decomposes is served, and not announced.</summary>
    [Fact]
    public async Task AnUnchangedResendIsServedButNotAnnounced()
    {
        SetupDecomposed([false]);

        var created = await _service.CreateDeviceStatusesAsync([Posted(Id)]);

        created.Should().ContainSingle().Which.Uploader!.Battery.Should().Be(64);
        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A batch that fails after its snapshots committed announces the statuses those writes created or
    /// updated, and not a status it found already stored.
    /// </summary>
    [Fact]
    public async Task ARequestCancelledMidBatchAnnouncesWhatItsCommittedWritesWrote()
    {
        using var request = new CancellationTokenSource();
        request.Cancel();
        _uploaders
            .Setup(r => r.GetByLegacyIdAsync(OtherId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploaderSnapshot { Id = Guid.CreateVersion7(), LegacyId = OtherId, Timestamp = At, Battery = 30 });
        SetupDecomposed([true, false], thenThrow: new OperationCanceledException(request.Token));

        var create = () => _service.CreateDeviceStatusesAsync([Posted(Id), Posted(OtherId)], cancellationToken: request.Token);

        await create.Should().ThrowAsync<OperationCanceledException>();
        _sideEffects.Verify(s => s.OnCreatedAsync(
            "devicestatus",
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Single().Id == Id && l.Single().Uploader!.Battery == 64),
            It.IsAny<WriteEffectOptions>(),
            It.Is<CancellationToken>(t => !t.IsCancellationRequested)), Times.Once);
        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Single().Id == Id),
            It.Is<CancellationToken>(t => !t.IsCancellationRequested)), Times.Once);
    }

    /// <summary>A batch that fails before any write commits announces nothing, though its statuses read back.</summary>
    [Fact]
    public async Task ABatchThatFailsBeforeItsWritesAnnouncesNothing()
    {
        _decomposer
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>(), It.IsAny<DeviceStatusDecomposition?>()))
            .ThrowsAsync(new TimeoutException("device read"));

        var create = () => _service.CreateDeviceStatusesAsync([Posted(Id)]);

        await create.Should().ThrowAsync<TimeoutException>();
        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ABackfillIsWrittenWithoutAnAnnouncement()
    {
        await _service.CreateDeviceStatusesAsync([Posted(Id)], origin: WriteOrigin.Backfill);

        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.IsAny<IReadOnlyList<DeviceStatus>>(), null, WriteOrigin.Backfill, It.IsAny<CancellationToken>(), It.IsAny<DeviceStatusDecomposition?>()));
        VerifyNothingAnnounced();
    }

    [Fact]
    public async Task AConnectorsLiveStatusesAreAnnouncedWithTheirSource()
    {
        var created = await _service.CreateDeviceStatusesAsync([Posted(Id)], DataSources.NightscoutConnector);

        var served = created.Should().ContainSingle().Subject;
        served.DataSource.Should().Be(DataSources.NightscoutConnector);
        VerifyAnnounced(served);
        _decomposer.Verify(d => d.DecomposeBatchAsync(
            It.IsAny<IReadOnlyList<DeviceStatus>>(), DataSources.NightscoutConnector, WriteOrigin.Live, It.IsAny<CancellationToken>(), It.IsAny<DeviceStatusDecomposition?>()));
    }

    [Fact]
    public async Task AConnectorsStatusThatWroteNothingIsNotAnnounced()
    {
        SetupDecomposed([false, false], skippedDeleted: 2);

        var created = await _service.CreateDeviceStatusesAsync([Posted(Id), Posted(OtherId)], DataSources.NightscoutConnector);

        created.SkippedDeleted.Should().Be(2);
        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AConnectorsBackfillIsWrittenWithoutAnAnnouncement()
    {
        await _service.CreateDeviceStatusesAsync([Posted(Id)], DataSources.NightscoutConnector, WriteOrigin.Backfill);

        VerifyNothingAnnounced();
    }

    private void VerifyAnnounced(DeviceStatus served)
    {
        _sideEffects.Verify(s => s.OnCreatedAsync(
            "devicestatus",
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Single() == served),
            It.IsAny<WriteEffectOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.Single() == served), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyNothingAnnounced()
    {
        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
        _uploaders.Verify(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Has the decomposer fill the service's decomposition: each status a group of its own, with an
    /// uploader snapshot created where <paramref name="wrote"/> says so and found unchanged where not,
    /// every status writing when it is null. A <paramref name="thenThrow"/> is thrown once filled.
    /// </summary>
    private void SetupDecomposed(IReadOnlyList<bool>? wrote = null, int skippedDeleted = 0, Exception? thenThrow = null) =>
        _decomposer
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>(), It.IsAny<DeviceStatusDecomposition?>()))
            .Returns((IReadOnlyList<DeviceStatus> statuses, string? _, WriteOrigin _, CancellationToken _, DeviceStatusDecomposition? into) =>
            {
                var result = into ?? new DeviceStatusDecomposition();
                var groups = statuses.Select(_ => Guid.CreateVersion7()).ToList();
                result.Begin(groups);
                for (var i = 0; i < groups.Count; i++)
                {
                    var snapshot = new UploaderSnapshot { CorrelationId = groups[i] };
                    if (wrote?[i] ?? true)
                        result.CreatedRecords.Add(snapshot);
                    else
                        result.UnchangedRecords.Add(snapshot);
                }

                result.SkippedDeleted = skippedDeleted;
                return thenThrow is null ? Task.FromResult(result) : Task.FromException<DeviceStatusDecomposition>(thenThrow);
            });

    private static DeviceStatus Posted(string id) =>
        new() { Id = id, Device = "loop://iphone", CreatedAt = "2026-10-01T08:00:00.000Z" };
}
