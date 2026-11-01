using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The pump-suspension fold inside <see cref="DeviceStatusDecomposer"/>: <c>false → true</c> opens,
/// <c>true → false</c> closes, equal states and re-decompositions do nothing, a first observation
/// anchors a span, the pump clock is preferred for the transition, and the prior snapshot is the
/// newest stored strictly before the new one.
/// </summary>
public class DeviceStatusDecomposerPumpSuspensionTests
{
    private static readonly DateTime Noon = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IPumpSnapshotRepository> _pumpRepoMock = new();
    private readonly Mock<IStateSpanService> _stateSpanServiceMock = new();
    private readonly List<V4Models.PumpSnapshot> _stored = [];
    private readonly List<StateSpan> _spans;
    private readonly HashSet<string> _deleted = [];
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerPumpSuspensionTests()
    {
        _spans = _stateSpanServiceMock.BacksWith([], _deleted);
        _pumpRepoMock
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.PumpSnapshot> records, WriteOrigin _, CancellationToken _) =>
            {
                var written = records.ToList();
                foreach (var record in written)
                {
                    if (_stored.FirstOrDefault(s => s.LegacyId == record.LegacyId) is { } existing)
                        record.Id = existing.Id;
                    else if (record.Id == Guid.Empty)
                        record.Id = Guid.NewGuid();
                }

                return [.. written];
            });
        _pumpRepoMock
            .Setup(r => r.GetTimelineAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. _stored]);

        _decomposer = new DeviceStatusDecomposer(
            Mock.Of<IApsSnapshotRepository>(),
            _pumpRepoMock.Object,
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            _stateSpanServiceMock.Object,
            new UnresolvedDevices(),
            Mock.Of<IAuditContext>(),
            Mock.Of<ILoopClosedLoopStamper>(),
            TimeProvider.System, NullLogger<DeviceStatusDecomposer>.Instance);
    }

    private static DeviceStatus MakeDeviceStatus(string id, DateTime at, bool suspended, string? clockIso = null) => new()
    {
        Id = id,
        Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
        Device = "openaps://test",
        Pump = new PumpStatus
        {
            Manufacturer = "Insulet",
            Model = "Omnipod",
            Status = new PumpStatusDetails { Suspended = suspended },
            Clock = clockIso,
        },
    };

    private void Stored(DateTime at, bool suspended) =>
        _stored.Add(new V4Models.PumpSnapshot { Id = Guid.NewGuid(), Timestamp = at, Suspended = suspended });

    private StateSpan OpenSuspension(string originalId, DateTime start)
    {
        var span = new StateSpan
        {
            Id = originalId,
            OriginalId = originalId,
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Suspended.ToString(),
            StartTimestamp = start,
        };
        _spans.Add(span);
        return span;
    }

    private IEnumerable<StateSpan> Suspensions =>
        _spans.Where(s => s.Category == StateSpanCategory.PumpMode && s.State == PumpModeState.Suspended.ToString());

    [Fact]
    public async Task FalseToTrue_OpensSpan()
    {
        Stored(Noon.AddHours(-1), suspended: false);

        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy1", Noon, suspended: true)], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle(span =>
            span.EndTimestamp == null && span.StartTimestamp == Noon && span.OriginalId!.StartsWith("pump-suspended:"));
    }

    /// <remarks>
    /// The spans are derived from the snapshots the batch stored, so a refused write (the user deleted
    /// the snapshot this legacy id names) leaves nothing to transition from.
    /// </remarks>
    [Fact]
    public async Task WhenTheSnapshotWriteIsRefused_DerivesNoSpans()
    {
        Stored(Noon.AddHours(-1), suspended: false);
        _pumpRepoMock
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkWrite<V4Models.PumpSnapshot>([], skippedDeleted: 1));

        var result = await _decomposer.DecomposeBatchAsync(
            [MakeDeviceStatus("legacy-refused", Noon, suspended: true)], null, WriteOrigin.Live);

        result.CreatedRecords.Should().NotContain(r => r is V4Models.PumpSnapshot, "the refused snapshot is not reported as created");
        result.SkippedDeleted.Should().Be(1);
        _spans.Should().BeEmpty();
    }

    [Fact]
    public async Task TrueToFalse_ClosesOpenSpan()
    {
        Stored(Noon.AddHours(-1), suspended: true);
        var open = OpenSuspension("span-1", Noon.AddHours(-1));

        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy2", Noon, suspended: false)], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle(span => span.Id == open.Id && span.EndTimestamp == Noon);
    }

    [Fact]
    public async Task TrueToTrue_NoOp()
    {
        Stored(Noon.AddHours(-1), suspended: true);

        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy3", Noon, suspended: true)], null, WriteOrigin.Live);

        _spans.Should().BeEmpty();
    }

    [Fact]
    public async Task FalseToFalse_NoOp()
    {
        Stored(Noon.AddHours(-1), suspended: false);

        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy4", Noon, suspended: false)], null, WriteOrigin.Live);

        _spans.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstObservationTrue_OpensSpan()
    {
        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy5", Noon, suspended: true)], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle(span => span.EndTimestamp == null && span.StartTimestamp == Noon);
    }

    [Fact]
    public async Task FirstObservationFalse_DoesNotOpenSpan()
    {
        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy6", Noon, suspended: false)], null, WriteOrigin.Live);

        _spans.Should().BeEmpty();
    }

    [Fact]
    public async Task PumpClockPreferred_OverIngestTimestamp()
    {
        Stored(Noon.AddHours(-1), suspended: false);
        var pumpClock = new DateTime(2024, 1, 1, 11, 30, 0, DateTimeKind.Utc);

        await _decomposer.DecomposeBatchAsync(
            [MakeDeviceStatus("legacy7", Noon, suspended: true, clockIso: "2024-01-01T11:30:00Z")], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle(span => span.StartTimestamp == pumpClock);
    }

    /// <summary>
    /// A delayed upload between two stored snapshots (10:00 not suspended, 12:00 suspended with a span
    /// open) compares against 10:00, the newest strictly before it, not 12:00: no transition, and the
    /// open span stays open.
    /// </summary>
    [Fact]
    public async Task OutOfOrderIngestion_UsesLatestPriorByTimestamp()
    {
        Stored(Noon.AddHours(-2), suspended: false);
        Stored(Noon, suspended: true);
        OpenSuspension("open-at-noon", Noon);

        await _decomposer.DecomposeBatchAsync(
            [MakeDeviceStatus("legacy-ooo", Noon.AddHours(-1), suspended: false)], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle(span => span.EndTimestamp == null);
        _pumpRepoMock.Verify(
            r => r.GetTimelineAsync(Noon.AddHours(-1), Noon.AddHours(-1), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A re-sent resume ends at the transition its first decomposition ended the span at, so it neither
    /// backfills a second suspension nor ends a later one.
    /// </summary>
    [Fact]
    public async Task ResentResume_LeavesTheTimelineAsItWas()
    {
        Stored(Noon.AddHours(-2), suspended: true);
        Stored(Noon.AddHours(-1), suspended: true);
        OpenSuspension("first", Noon.AddHours(-2));
        var resume = MakeDeviceStatus("legacy-resume", Noon, suspended: false);
        await _decomposer.DecomposeBatchAsync([resume], null, WriteOrigin.Live);
        Stored(Noon, suspended: false);
        Stored(Noon.AddHours(1), suspended: true);
        OpenSuspension("later", Noon.AddHours(1));

        await _decomposer.DecomposeBatchAsync([resume], null, WriteOrigin.Live);

        Suspensions.Select(s => (s.OriginalId, s.EndTimestamp)).Should().BeEquivalentTo(
            [("first", (DateTime?)Noon), ("later", (DateTime?)null)]);
    }

    /// <summary>
    /// A re-decomposed suspension already opened its span, so a second decomposition opens none, even
    /// after a resume closed it.
    /// </summary>
    [Fact]
    public async Task IdempotentOpen_ASecondDecompositionOpensNoSecondSpan()
    {
        Stored(Noon.AddHours(-1), suspended: false);
        var ds = MakeDeviceStatus("legacy8", Noon, suspended: true);

        var first = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        _stored.Add(first.CreatedRecords.OfType<V4Models.PumpSnapshot>().Single());
        _spans.Single().EndTimestamp = Noon.AddHours(1);
        await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle().Which.EndTimestamp.Should().Be(Noon.AddHours(1));
    }

    /// <summary>
    /// The snapshots and the spans commit separately, so a span write that fails leaves the snapshot
    /// stored; its re-send reads it back unchanged and still folds it, writing the lost span.
    /// </summary>
    [Fact]
    public async Task SpanWriteFails_AnUnchangedResendWritesTheLostSpan()
    {
        Stored(Noon.AddHours(-1), suspended: false);
        var ds = MakeDeviceStatus("legacy-repair", Noon, suspended: true);
        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("span write"));

        var failed = () => _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        await failed.Should().ThrowAsync<TimeoutException>();
        _spans.Should().BeEmpty();

        _stateSpanServiceMock.BacksWith(_spans, _deleted);
        _pumpRepoMock
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.PumpSnapshot> records, WriteOrigin _, CancellationToken _) =>
            {
                var unchanged = records.ToList();
                return new BulkWrite<V4Models.PumpSnapshot>(unchanged, 0) { Unchanged = unchanged };
            });

        var resend = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        resend.UnchangedRecords.Should().ContainSingle(r => r is V4Models.PumpSnapshot);
        Suspensions.Should().ContainSingle(span => span.StartTimestamp == Noon && span.EndTimestamp == null);
    }

    /// <summary>
    /// A suspension span the user deleted holds its key, so a re-send of the status that opened it
    /// writes nothing and is not reported as written, however often it is re-sent.
    /// </summary>
    [Fact]
    public async Task ResendAfterTheUserDeletedItsSpan_WritesNothing()
    {
        Stored(Noon.AddHours(-1), suspended: false);
        var ds = MakeDeviceStatus("legacy-deleted-span", Noon, suspended: true);
        var first = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);
        _stored.Add(first.CreatedRecords.OfType<V4Models.PumpSnapshot>().Single());
        var span = _spans.Single();
        _spans.Remove(span);
        _deleted.Add(span.OriginalId!);
        _pumpRepoMock
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.PumpSnapshot> records, WriteOrigin _, CancellationToken _) =>
            {
                var unchanged = records.Select(r => { r.Id = _stored[^1].Id; return r; }).ToList();
                return new BulkWrite<V4Models.PumpSnapshot>(unchanged, 0) { Unchanged = unchanged };
            });

        var resend = await _decomposer.DecomposeBatchAsync([ds], null, WriteOrigin.Live);

        resend.Wrote.Should().Equal(false);
        resend.CreatedRecords.Should().BeEmpty();
        resend.UpdatedRecords.Should().BeEmpty();
        _spans.Should().BeEmpty();
    }


    /// not a pump clock, so the second cannot see the first as prior and reads a false → true
    /// transition. At most one Suspended span is open: a transition while one is open opens none.
    /// </summary>
    [Fact]
    public async Task FalseToTrue_WhenSuspendedSpanAlreadyOpen_DoesNotOpenDuplicate()
    {
        Stored(Noon.AddHours(-1), suspended: false);
        OpenSuspension("open-1", Noon.AddMinutes(-30));

        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy-dup", Noon, suspended: true)], null, WriteOrigin.Live);

        Suspensions.Should().ContainSingle(span => span.Id == "open-1" && span.EndTimestamp == null);
    }

    /// <summary>
    /// Duplicate open Suspended spans left before the open guard are all closed by a resume, so no
    /// leftover keeps the pump latched suspended.
    /// </summary>
    [Fact]
    public async Task TrueToFalse_ClosesAllOpenSuspendedSpans()
    {
        Stored(Noon.AddHours(-1), suspended: true);
        OpenSuspension("open-1", Noon.AddHours(-1));
        OpenSuspension("open-2", Noon.AddHours(-1).AddSeconds(2));

        await _decomposer.DecomposeBatchAsync([MakeDeviceStatus("legacy-close-all", Noon, suspended: false)], null, WriteOrigin.Live);

        Suspensions.Should().HaveCount(2).And.OnlyContain(span => span.EndTimestamp == Noon);
    }
}
