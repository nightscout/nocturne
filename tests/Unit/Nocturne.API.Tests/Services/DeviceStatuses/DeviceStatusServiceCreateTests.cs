using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private readonly Mock<IDeviceStatusGroupRepository> _groups = new();
    private readonly Mock<IApsSnapshotRepository> _aps = new();
    private readonly Mock<IPumpSnapshotRepository> _pumps = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaders = new();
    private readonly Mock<IStateSpanRepository> _spans = new();
    private readonly Mock<IDeviceStatusExtrasRepository> _extras = new();
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<IDataEventSink<DeviceStatus>> _events = new();
    private readonly Mock<ILogger<DeviceStatusService>> _logger = new();
    private readonly DeviceStatusService _service;

    /// <summary>How the status sent under each id is stored, given the group the batch writes it under.</summary>
    private readonly Dictionary<string, Func<Guid, DeviceStatusGroup>> _storedAs = new()
    {
        [Id] = group => UploaderGroup(Id, group, battery: 64),
    };

    private readonly Dictionary<Guid, DeviceStatusGroup> _stored = [];

    public DeviceStatusServiceCreateTests()
    {
        SetupDecomposed();
        _groups
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<Guid> groups, CancellationToken _) => Task.FromResult<IReadOnlyList<DeviceStatusGroup>>(
                groups.Where(_stored.ContainsKey).Select(group => _stored[group]).ToList()));
        _spans
            .Setup(r => r.GetByCategory(It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _service = new DeviceStatusService(
            new DeviceStatusProjectionService(
                _groups.Object,
                _aps.Object,
                _pumps.Object,
                _uploaders.Object,
                _spans.Object,
                _extras.Object,
                TimeProvider.System,
                NullLogger<DeviceStatusProjectionService>.Instance),
            _decomposer.Object,
            _sideEffects.Object,
            _events.Object,
            _logger.Object);
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
        _storedAs[OtherId] = group => UploaderGroup(OtherId, group, battery: 30);
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

    /// <summary>
    /// The batch is read back in one read of its groups, whatever its size, and never status by status;
    /// each status is served as read, or as given when it has no id, in the order it was sent.
    /// </summary>
    [Fact]
    public async Task ABatchIsReadBackInOneReadOfItsGroups_InTheOrderItWasSent()
    {
        var ids = Enumerable.Range(0, 6).Select(i => $"65f0a1b2c3d4e5f60123460{i}").ToList();
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
            _storedAs[id] = group => new DeviceStatusGroup(
                new ApsSnapshot
                {
                    Id = Guid.CreateVersion7(),
                    LegacyId = id,
                    CorrelationId = group,
                    Timestamp = At.AddMinutes(i),
                    AidAlgorithm = AidAlgorithm.OpenAps,
                    Iob = i,
                },
                null, null, null, At);
        var unnamed = new DeviceStatus { Device = "loop://iphone", CreatedAt = "2026-10-01T08:00:00.000Z" };
        List<DeviceStatus> posted = [.. ids.Take(3).Select(Posted), unnamed, .. ids.Skip(3).Select(Posted)];

        var created = await _service.CreateDeviceStatusesAsync(posted);

        created.Select(s => s.Id).Should().Equal([.. ids.Take(3), null, .. ids.Skip(3)]);
        created.Where(s => s.Id is not null).Select(s => s.OpenAps!.Iob!.Iob).Should().Equal(0, 1, 2, 3, 4, 5);
        created[3].Should().BeSameAs(unnamed);
        _groups.Verify(r => r.GetByCorrelationIdsAsync(
            It.Is<IReadOnlyCollection<Guid>>(groups => groups.Count == ids.Count), It.IsAny<CancellationToken>()), Times.Once);
        _spans.Verify(r => r.GetByCategory(
            StateSpanCategory.Override, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _groups.VerifyNoOtherCalls();
        _aps.VerifyNoOtherCalls();
        _pumps.VerifyNoOtherCalls();
        _uploaders.VerifyNoOtherCalls();
        _extras.VerifyNoOtherCalls();
    }

    /// <summary>The write has committed, so a read-back that fails serves and announces every status as given.</summary>
    [Fact]
    public async Task AReadBackThatFails_ServesEveryStatusAsGiven_AndWarns()
    {
        _groups
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("read back"));
        List<DeviceStatus> posted = [Posted(Id), Posted(OtherId)];

        var created = await _service.CreateDeviceStatusesAsync(posted, DataSources.NightscoutConnector);

        created.Should().Equal(posted, ReferenceEquals);
        created.Should().OnlyContain(s => s.DataSource == DataSources.NightscoutConnector);
        _events.Verify(e => e.OnCreatedAsync(
            It.Is<IReadOnlyList<DeviceStatus>>(l => l.SequenceEqual(posted)), It.IsAny<CancellationToken>()), Times.Once);
        _logger.Verify(l => l.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<TimeoutException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    /// <summary>
    /// A created status is served as list and history reads serve it: its <c>srvModified</c> is its
    /// group's stamp, the newest change to any of its rows, and not its anchoring row's own.
    /// </summary>
    [Fact]
    public async Task ACreatedStatusIsServedWithItsGroupStamp_AsHistoryReadsServeIt()
    {
        var pumpWrittenLater = At.AddMilliseconds(5);
        _storedAs[Id] = group => new DeviceStatusGroup(
            new ApsSnapshot
            {
                Id = Guid.CreateVersion7(),
                LegacyId = Id,
                CorrelationId = group,
                Timestamp = At,
                ModifiedAt = At,
                AidAlgorithm = AidAlgorithm.OpenAps,
            },
            new PumpSnapshot { Id = Guid.CreateVersion7(), LegacyId = Id, CorrelationId = group, Timestamp = At, ModifiedAt = pumpWrittenLater },
            null, null, pumpWrittenLater);

        var created = await _service.CreateDeviceStatusesAsync([Posted(Id)]);

        created.Single().SrvModified.Should().Be(new DateTimeOffset(pumpWrittenLater).ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// A created status is served the override active at its time only when it has an APS snapshot,
    /// as list reads give it.
    /// </summary>
    [Fact]
    public async Task ACreatedStatusCarriesTheOverrideOnlyWithAnApsSnapshot_AsListReadsGiveIt()
    {
        _storedAs[Id] = group => new DeviceStatusGroup(
            new ApsSnapshot
            {
                Id = Guid.CreateVersion7(),
                LegacyId = Id,
                CorrelationId = group,
                Timestamp = At,
                AidAlgorithm = AidAlgorithm.OpenAps,
            },
            null, null, null, At);
        _storedAs[OtherId] = group => new DeviceStatusGroup(
            null, new PumpSnapshot { Id = Guid.CreateVersion7(), LegacyId = OtherId, CorrelationId = group, Timestamp = At, Reservoir = 50 },
            null, null, At);
        _spans
            .Setup(r => r.GetByCategory(StateSpanCategory.Override, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new StateSpan { Category = StateSpanCategory.Override, StartTimestamp = At.AddMinutes(-5) }]);

        var created = await _service.CreateDeviceStatusesAsync([Posted(Id), Posted(OtherId)]);

        created.Select(s => s.Override is not null).Should().Equal(true, false);
        created[1].Pump!.Reservoir.Should().Be(50);
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
        _groups.Verify(
            r => r.GetByCorrelationIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Has the decomposer fill the service's decomposition: each status a group of its own, stored as
    /// <see cref="_storedAs"/> gives its id, with an uploader snapshot created where
    /// <paramref name="wrote"/> says so and found unchanged where not, every status writing when it is
    /// null. A <paramref name="thenThrow"/> is thrown once filled.
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
                    if (statuses[i].Id is { } id && _storedAs.TryGetValue(id, out var storedAs))
                        _stored[groups[i]] = storedAs(groups[i]);

                    var snapshot = new UploaderSnapshot { CorrelationId = groups[i] };
                    if (wrote?[i] ?? true)
                        result.CreatedRecords.Add(snapshot);
                    else
                        result.UnchangedRecords.Add(snapshot);
                }

                result.SkippedDeleted = skippedDeleted;
                return thenThrow is null ? Task.FromResult(result) : Task.FromException<DeviceStatusDecomposition>(thenThrow);
            });

    private static DeviceStatusGroup UploaderGroup(string id, Guid group, int battery) => new(
        null, null,
        new UploaderSnapshot { Id = Guid.CreateVersion7(), LegacyId = id, CorrelationId = group, Timestamp = At, Battery = battery },
        null, At);

    private static DeviceStatus Posted(string id) =>
        new() { Id = id, Device = "loop://iphone", CreatedAt = "2026-10-01T08:00:00.000Z" };
}
