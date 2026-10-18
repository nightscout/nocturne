using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Npgsql;

namespace Nocturne.API.Tests.Services.Devices;

public class SensorSessionInferenceServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddHours(-48);
    private static readonly DateTime OldSensorEnds = Now.AddHours(-30);
    private static readonly DateTime NewSensorBegins = OldSensorEnds.AddMinutes(40);

    private static readonly PatientDevice G7 = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DeviceId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        DeviceCategory = DeviceCategory.CGM,
        CatalogId = "dexcom-g7",
        IsCurrent = true,
    };

    private static readonly Guid OtherDevice = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IPatientDeviceRepository> _patientDevices = new();
    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<IDeviceEventRepository> _events = new();
    private readonly Mock<IDeviceEventReactor> _reactor = new();
    private readonly List<DeviceEvent> _storedEvents = [];
    private readonly List<DeviceEvent> _deleted = [];
    private List<PatientDevice> _devices = [G7];
    private List<SensorGlucose> _readings = [.. Run(Now.AddHours(-40), OldSensorEnds), .. Run(NewSensorBegins, Now)];
    private readonly SensorSessionInferenceService _service;

    public SensorSessionInferenceServiceTests()
    {
        _patientDevices
            .Setup(p => p.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _devices);

        _glucose
            .Setup(g => g.GetAttributedReadingTimesAsync(G7.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, DateTime from, DateTime to, CancellationToken _) =>
                _readings.Where(r => r.Timestamp >= from && r.Timestamp <= to).ToList());

        _events
            .Setup(e => e.GetAllCopiesAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, IReadOnlyCollection<DeviceEventType> types, CancellationToken _) =>
                _storedEvents.Where(e => types.Contains(e.EventType) && e.Timestamp >= from && e.Timestamp < to).ToList());
        _events
            .Setup(e => e.GetUserDeletedAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, IReadOnlyCollection<DeviceEventType> types, CancellationToken _) =>
                _deleted.Where(e => types.Contains(e.EventType) && e.Timestamp >= from && e.Timestamp < to).ToList());
        _events
            .Setup(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceEvent e, WriteOrigin _, CancellationToken _) => e);
        // Every withdrawn start began a tracker run still going, unless a test says otherwise.
        _reactor
            .Setup(r => r.GetFollowedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.ToHashSet());

        _service = new SensorSessionInferenceService(
            _patientDevices.Object, _glucose.Object, _events.Object, _reactor.Object, new FakeTimeProvider(Now),
            NullLogger<SensorSessionInferenceService>.Instance);
    }

    private static IEnumerable<SensorGlucose> Run(DateTime from, DateTime to)
    {
        for (var t = from; t <= to; t = t.AddMinutes(5))
            yield return new SensorGlucose { Timestamp = t, UtcOffset = 600 };
    }

    private static string SyncIdentifierOfTheChange =>
        $"{G7.Id:N}:{new DateTimeOffset(NewSensorBegins, TimeSpan.Zero).ToUnixTimeMilliseconds()}";

    /// <summary>The sync key of a G7 start inferred at <paramref name="startedAt"/>: its first reading follows a warm-up later.</summary>
    private static string KeyFor(DateTime startedAt) =>
        $"{G7.Id:N}:{new DateTimeOffset(startedAt.AddMinutes(30), TimeSpan.Zero).ToUnixTimeMilliseconds()}";

    private DeviceEvent Inferred(string syncIdentifier, DateTime at) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = at,
        EventType = DeviceEventType.SensorStart,
        PatientDeviceId = G7.Id,
        DataSource = DataSources.SensorSessionInference,
        SyncIdentifier = syncIdentifier,
    };

    private void VerifyNothingWritten()
    {
        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AWarmupGap_WritesALiveInferredSensorStartForTheDevice()
    {
        await _service.InferAsync(From, Now);

        _events.Verify(e => e.CreateAsync(
            It.Is<DeviceEvent>(d =>
                d.Timestamp == NewSensorBegins.AddMinutes(-30)
                && d.UtcOffset == 600
                && d.EventType == DeviceEventType.SensorStart
                && d.PatientDeviceId == G7.Id
                && d.DeviceId == G7.DeviceId
                && d.DataSource == DataSources.SensorSessionInference
                && d.SyncIdentifier == SyncIdentifierOfTheChange),
            WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AStartAlreadyInferred_IsNotWrittenAgain()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));

        await _service.InferAsync(From, Now);

        VerifyNothingWritten();
    }

    [Fact]
    public async Task AStartTheUserDeleted_IsLeftDeleted()
    {
        _events
            .Setup(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(RecreationBlockedException.ForSyncKey(nameof(DeviceEvent), DataSources.SensorSessionInference, SyncIdentifierOfTheChange));

        var infer = () => _service.InferAsync(From, Now);

        await infer.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AnInferredStartWhoseGapHasFilledIn_IsWithdrawn()
    {
        var staleIdentifier = KeyFor(Now.AddHours(-36));
        _storedEvents.Add(Inferred(staleIdentifier, Now.AddHours(-36)));

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, staleIdentifier, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnInferredStartOutsideTheWindow_IsLeftAlone()
    {
        _storedEvents.Add(Inferred(KeyFor(From.AddMinutes(-1)), From.AddMinutes(-1)));

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ARecordedChangeArrivingAfterTheInference_WithdrawsIt()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));
        _storedEvents.Add(new DeviceEvent
        {
            Timestamp = OldSensorEnds.AddMinutes(10),
            EventType = DeviceEventType.SensorChange,
            PatientDeviceId = G7.Id,
            DataSource = DataSources.XDrip,
        });

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, SyncIdentifierOfTheChange, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnUnattributedRecordedStart_AccountsForTheGap()
    {
        _storedEvents.Add(new DeviceEvent
        {
            Timestamp = OldSensorEnds.AddMinutes(10),
            EventType = DeviceEventType.SensorStart,
            DataSource = DataSources.XDrip,
        });

        await _service.InferAsync(From, Now);

        VerifyNothingWritten();
    }

    [Fact]
    public async Task AnotherDevicesRecordedStart_DoesNotAccountForTheGap()
    {
        _storedEvents.Add(new DeviceEvent
        {
            Timestamp = OldSensorEnds.AddMinutes(10),
            EventType = DeviceEventType.SensorStart,
            PatientDeviceId = OtherDevice,
            DataSource = DataSources.XDrip,
        });

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ADifferentKindOfDeviceEvent_DoesNotAccountForTheGap()
    {
        _storedEvents.Add(new DeviceEvent
        {
            Timestamp = OldSensorEnds.AddMinutes(10),
            EventType = DeviceEventType.SiteChange,
            DataSource = DataSources.XDrip,
        });

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(DeviceCategory.InsulinPump, "dexcom-g7")]
    [InlineData(DeviceCategory.CGM, null)]
    [InlineData(DeviceCategory.CGM, "no-such-cgm")]
    public async Task ADeviceThatIsNotACataloguedCgm_IsSkipped(DeviceCategory category, string? catalogId)
    {
        _devices = [new PatientDevice { Id = G7.Id, DeviceCategory = category, CatalogId = catalogId }];

        await _service.InferAsync(From, Now);

        _glucose.VerifyNoOtherCalls();
        VerifyNothingWritten();
    }

    /// <summary>
    /// Backfill landing in a dismissed dropout leaves a shorter gap that can sit anywhere inside it,
    /// with a new first reading, a new sync key and nothing in common with the deleted start's time.
    /// </summary>
    [Fact]
    public async Task AStartTheUserDismissed_StaysDismissedWhenBackfillShrinksItsGap()
    {
        _deleted.Add(Inferred($"{G7.Id:N}:1", OldSensorEnds.AddHours(2.5)));
        _readings = [.. Run(Now.AddHours(-40), OldSensorEnds), .. Run(OldSensorEnds.AddHours(1), Now)];

        await _service.InferAsync(From, Now);

        VerifyNothingWritten();
    }

    [Fact]
    public async Task ARecordedStartTheUserDeleted_DismissesTheGapItAccountedFor()
    {
        _deleted.Add(new DeviceEvent
        {
            Timestamp = OldSensorEnds.AddMinutes(10),
            EventType = DeviceEventType.SensorStart,
            PatientDeviceId = G7.Id,
            DataSource = DataSources.XDrip,
        });

        await _service.InferAsync(From, Now);

        VerifyNothingWritten();
    }

    /// <summary>Deleting a device registration leaves its events unattributed.</summary>
    [Fact]
    public async Task AnUnattributedDismissal_DismissesTheGap()
    {
        var dismissal = Inferred($"{OtherDevice:N}:1", NewSensorBegins.AddMinutes(-30));
        dismissal.PatientDeviceId = null;
        _deleted.Add(dismissal);

        await _service.InferAsync(From, Now);

        VerifyNothingWritten();
    }

    [Fact]
    public async Task AnotherDevicesDismissal_DoesNotDismissTheGap()
    {
        var dismissal = Inferred($"{OtherDevice:N}:1", NewSensorBegins.AddMinutes(-30));
        dismissal.PatientDeviceId = OtherDevice;
        _deleted.Add(dismissal);

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(40 + 6 * 60 + 1)]
    public async Task ADismissedStartWhoseLongestGapCannotReachThisOne_DoesNotDismissIt(int minutesAfterTheOldSensorEnds)
    {
        _deleted.Add(Inferred($"{G7.Id:N}:1", OldSensorEnds.AddMinutes(minutesAfterTheOldSensorEnds)));

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private DeviceEvent Recorded(DateTime at, string dataSource = DataSources.XDrip) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = at,
        EventType = DeviceEventType.SensorChange,
        PatientDeviceId = G7.Id,
        DataSource = dataSource,
    };

    /// <summary>
    /// A recorded change dated before the inferred start was passed over as history when it arrived,
    /// so once the inferred start is withdrawn it has to be put to the trackers again.
    /// </summary>
    [Fact]
    public async Task WithdrawingAStartARecordedChangeAccountsFor_ReplaysTheRecordedChange()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));
        var recorded = Recorded(OldSensorEnds.AddMinutes(-60));
        _storedEvents.Add(recorded);

        await _service.InferAsync(From, Now);

        _reactor.Verify(r => r.OnReplayedAsync(
            It.Is<IReadOnlyList<DeviceEvent>>(l => l.Count == 1 && l[0].Id == recorded.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Replaying_SendsOnlyTheFirstOfTwoSourcesCopies()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));
        var first = Recorded(OldSensorEnds.AddMinutes(5));
        _storedEvents.Add(first);
        _storedEvents.Add(Recorded(OldSensorEnds.AddMinutes(12), DataSources.AndroidAps));

        await _service.InferAsync(From, Now);

        _reactor.Verify(r => r.OnReplayedAsync(
            It.Is<IReadOnlyList<DeviceEvent>>(l => l.Count == 1 && l[0].Id == first.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Copies landing in a chain, each within the spread of the one before, are one change.</summary>
    [Fact]
    public async Task Replaying_TreatsAChainOfCopiesAsOneChange()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));
        var first = Recorded(OldSensorEnds.AddMinutes(-60));
        _storedEvents.Add(first);
        _storedEvents.Add(Recorded(OldSensorEnds.AddMinutes(-52), DataSources.AndroidAps));
        _storedEvents.Add(Recorded(OldSensorEnds.AddMinutes(-44), DataSources.Loop));

        await _service.InferAsync(From, Now);

        _reactor.Verify(r => r.OnReplayedAsync(
            It.Is<IReadOnlyList<DeviceEvent>>(l => l.Count == 1 && l[0].Id == first.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WithdrawingAStartWhoseGapFilledIn_ReplaysNothing()
    {
        _storedEvents.Add(Inferred(KeyFor(Now.AddHours(-36)), Now.AddHours(-36)));

        await _service.InferAsync(From, Now);

        VerifyNothingReplayed();
    }

    [Fact]
    public async Task WithoutAWithdrawal_NothingIsReplayed()
    {
        _storedEvents.Add(Recorded(Now.AddHours(-20)));

        await _service.InferAsync(From, Now);

        VerifyNothingReplayed();
    }

    [Fact]
    public async Task AFailedReplay_DoesNotStopThePass()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));
        _storedEvents.Add(Recorded(OldSensorEnds.AddMinutes(-60)));
        _reactor
            .Setup(r => r.OnReplayedAsync(It.IsAny<IReadOnlyList<DeviceEvent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tracker repository unavailable"));

        var infer = () => _service.InferAsync(From, Now);

        await infer.Should().NotThrowAsync();
    }

    private static readonly DateTime OldGapEnds = Now.AddDays(-10);

    /// <summary>Twelve days of readings with a sensor change ten days ago and another thirty hours ago.</summary>
    private void GivenHistoryWithAnOldAndARecentChange() =>
        _readings =
        [
            .. Run(Now.AddDays(-12), OldGapEnds.AddMinutes(-40)),
            .. Run(OldGapEnds, OldSensorEnds),
            .. Run(NewSensorBegins, Now),
        ];

    [Fact]
    public async Task History_WritesOldStartsAsBackfillAndRecentOnesLive()
    {
        GivenHistoryWithAnOldAndARecentChange();

        await _service.InferHistoryAsync(G7);

        _events.Verify(e => e.CreateAsync(
            It.Is<DeviceEvent>(d => d.Timestamp == OldGapEnds.AddMinutes(-30)), WriteOrigin.Backfill, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.CreateAsync(
            It.Is<DeviceEvent>(d => d.Timestamp == NewSensorBegins.AddMinutes(-30)), WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>History older than the depth kept is never read.</summary>
    [Fact]
    public async Task History_ReadsNothingOlderThanItsDepth()
    {
        var reads = new List<DateTime>();
        _glucose
            .Setup(g => g.GetAttributedReadingTimesAsync(G7.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, DateTime from, DateTime _, CancellationToken _) => reads.Add(from))
            .ReturnsAsync([]);

        await _service.InferHistoryAsync(G7);

        reads.Should().NotBeEmpty();
        reads.Min().Should().BeOnOrAfter(Now - SensorSessionInferenceService.HistoryDepth - TimeSpan.FromDays(1));
    }

    /// <summary>Readings with one gap, and so one start, whose first reading resumes at <paramref name="resumes"/>.</summary>
    private void GivenOneChangeResumingAt(DateTime resumes) =>
        _readings = [.. Run(resumes.AddDays(-1), resumes.AddMinutes(-40)), .. Run(resumes, resumes.AddDays(1))];

    private static PatientDevice G7Registered(DateOnly? startDate = null, DateOnly? endDate = null) => new()
    {
        Id = G7.Id, DeviceId = G7.DeviceId, DeviceCategory = DeviceCategory.CGM, CatalogId = "dexcom-g7",
        StartDate = startDate, EndDate = endDate,
    };

    private void VerifyStartWritten(DateTime at, WriteOrigin origin) =>
        _events.Verify(e => e.CreateAsync(It.Is<DeviceEvent>(d => d.Timestamp == at), origin, It.IsAny<CancellationToken>()), Times.Once);

    private void VerifyStartsWritten(int count) =>
        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Exactly(count));

    /// <summary>A gap whose old sensor ends in one window of history and whose start lands in the next.</summary>
    [Fact]
    public async Task History_InfersAStartWhoseGapStraddlesAWindowBoundary()
    {
        var boundary = Now - SensorSessionInferenceService.HistoryDepth + TimeSpan.FromDays(30);
        GivenOneChangeResumingAt(boundary.AddMinutes(40));

        await _service.InferHistoryAsync(G7);

        VerifyStartWritten(boundary.AddMinutes(10), WriteOrigin.Backfill);
        VerifyStartsWritten(1);
    }

    [Fact]
    public async Task History_WritesAStartAtTheStartOfTheLiveWindowLive()
    {
        var liveFrom = Now - SensorSessionInferenceService.LiveWindow;
        GivenOneChangeResumingAt(liveFrom.AddMinutes(30));

        await _service.InferHistoryAsync(G7);

        VerifyStartWritten(liveFrom, WriteOrigin.Live);
        VerifyStartsWritten(1);
    }

    /// <summary>Registered dates are local, so the day before the start date may hold the device's readings.</summary>
    [Theory]
    [InlineData(-12, true)]
    [InlineData(-25, false)]
    public async Task History_ReachesADayBeforeTheRegisteredStartDate(int hoursFromStartDate, bool inferred)
    {
        var startDate = DateOnly.FromDateTime(Now.AddDays(-20));
        var startOfStartDate = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        GivenOneChangeResumingAt(startOfStartDate.AddHours(hoursFromStartDate).AddMinutes(30));

        await _service.InferHistoryAsync(G7Registered(startDate: startDate));

        VerifyStartsWritten(inferred ? 1 : 0);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(25, false)]
    public async Task History_ReachesADayAfterTheRegisteredEndDate(int hoursAfterEndDate, bool inferred)
    {
        var endDate = DateOnly.FromDateTime(Now.AddDays(-20));
        var endOfEndDate = endDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        GivenOneChangeResumingAt(endOfEndDate.AddHours(hoursAfterEndDate).AddMinutes(30));

        await _service.InferHistoryAsync(G7Registered(endDate: endDate));

        VerifyStartsWritten(inferred ? 1 : 0);
    }

    /// <summary>
    /// A start the background pass wrote live may have begun a tracker run still going, so its
    /// withdrawal in history is live too, and the recorded change that replaces it is replayed.
    /// </summary>
    [Fact]
    public async Task HistoryWithdrawals_AreLiveAndReplayTheRecordedChange()
    {
        GivenHistoryWithAnOldAndARecentChange();
        var stale = KeyFor(Now.AddDays(-20));
        _storedEvents.Add(Inferred(stale, Now.AddDays(-20)));
        var recorded = Recorded(Now.AddDays(-20).AddMinutes(-30));
        _storedEvents.Add(recorded);

        await _service.InferHistoryAsync(G7);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, stale, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _reactor.Verify(r => r.OnReplayedAsync(
            It.Is<IReadOnlyList<DeviceEvent>>(l => l.Any(e => e.Id == recorded.Id)), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>An edit that moves the start date later leaves the starts before it unexplained.</summary>
    [Fact]
    public async Task History_WithdrawsStartsBeforeTheRegisteredDates()
    {
        var resumes = Now.AddDays(-5);
        GivenOneChangeResumingAt(resumes);
        var kept = $"{G7.Id:N}:{new DateTimeOffset(resumes, TimeSpan.Zero).ToUnixTimeMilliseconds()}";
        _storedEvents.Add(Inferred($"{G7.Id:N}:early", Now.AddDays(-30)));
        _storedEvents.Add(Inferred(kept, resumes.AddMinutes(-30)));

        await _service.InferHistoryAsync(G7Registered(startDate: DateOnly.FromDateTime(Now.AddDays(-10))));

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, $"{G7.Id:N}:early", WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, kept, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task History_OfADeviceNoLongerACgm_WithdrawsItsInferredStarts()
    {
        _storedEvents.Add(Inferred($"{G7.Id:N}:1", Now.AddDays(-5)));

        await _service.InferHistoryAsync(new PatientDevice { Id = G7.Id, DeviceCategory = DeviceCategory.InsulinPump, CatalogId = "dexcom-g7" });

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, $"{G7.Id:N}:1", WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _glucose.VerifyNoOtherCalls();
        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Another pass over the same device wrote the start between this one reading and writing it.</summary>
    [Fact]
    public async Task AStartAnotherPassWroteFirst_DoesNotStopThePass()
    {
        GivenHistoryWithAnOldAndARecentChange();
        _events
            .Setup(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("duplicate", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));

        var infer = () => _service.InferHistoryAsync(G7);

        await infer.Should().NotThrowAsync();
        VerifyStartsWritten(2);
    }

    private void VerifyNothingReplayed() =>
        _reactor.Verify(r => r.OnReplayedAsync(It.IsAny<IReadOnlyList<DeviceEvent>>(), It.IsAny<CancellationToken>()), Times.Never);

    private void GivenNothingFollowed() =>
        _reactor
            .Setup(r => r.GetFollowedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());

    /// <summary>
    /// A withdrawn start no tracker run followed, a backfill from history say, undid nothing, so
    /// the recorded change beside it is not put to the trackers on its account.
    /// </summary>
    [Fact]
    public async Task WithdrawingAStartNoRunFollowed_ReplaysNothing()
    {
        GivenNothingFollowed();
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));
        _storedEvents.Add(Recorded(OldSensorEnds.AddMinutes(-60)));

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, SyncIdentifierOfTheChange, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNothingReplayed();
    }

    /// <summary>
    /// A start the user corrected to a time outside the window is still the window's start: it is
    /// judged by its gap, not by where the user moved it.
    /// </summary>
    [Fact]
    public async Task AStartTheUserMovedOutOfTheWindow_IsNotWithdrawnForIt()
    {
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, From.AddHours(-3)));

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        _events.Verify(e => e.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A start the user moved into the window from elsewhere belongs to the window its gap is in, so
    /// this window does not take it for a stale one of its own.
    /// </summary>
    [Fact]
    public async Task AStartTheUserMovedIntoTheWindow_IsLeftToItsOwnWindow()
    {
        var elsewhere = $"{G7.Id:N}:{new DateTimeOffset(From.AddDays(-5), TimeSpan.Zero).ToUnixTimeMilliseconds()}";
        _storedEvents.Add(Inferred(elsewhere, Now.AddHours(-10)));

        await _service.InferAsync(From, Now);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, elsewhere, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Readings stay stamped to a device whose start date an edit moved later, so the background pass
    /// keeps to its registered dates or it would infer back what the edit withdrew.
    /// </summary>
    [Fact]
    public async Task TheBackgroundPass_InfersNothingBeforeTheDevicesRegisteredStart()
    {
        _devices = [G7Registered(startDate: DateOnly.FromDateTime(Now.AddDays(1)))];

        await _service.InferAsync(From, Now);

        VerifyStartsWritten(0);
    }

    [Fact]
    public async Task ForgettingADevice_WithdrawsAllItsInferredStarts()
    {
        _storedEvents.Add(Inferred($"{G7.Id:N}:1", Now.AddDays(-5)));
        _storedEvents.Add(Inferred(SyncIdentifierOfTheChange, NewSensorBegins.AddMinutes(-30)));

        await _service.ForgetDeviceAsync(G7);

        _events.Verify(e => e.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, It.IsAny<string>(), WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Exactly(2));
        VerifyStartsWritten(0);
    }

    /// <summary>
    /// A start withdrawn because an edit narrowed the dates may have begun a run, and the recorded
    /// change beside it takes that run's place.
    /// </summary>
    [Fact]
    public async Task AStartOutsideNarrowedDates_ReplaysTheRecordedChangeBesideIt()
    {
        var early = Now.AddDays(-30);
        _storedEvents.Add(Inferred($"{G7.Id:N}:{new DateTimeOffset(early.AddMinutes(30), TimeSpan.Zero).ToUnixTimeMilliseconds()}", early));
        var recorded = Recorded(early.AddMinutes(-20));
        _storedEvents.Add(recorded);

        await _service.InferHistoryAsync(G7Registered(startDate: DateOnly.FromDateTime(Now.AddDays(-10))));

        _reactor.Verify(r => r.OnReplayedAsync(
            It.Is<IReadOnlyList<DeviceEvent>>(l => l.Any(e => e.Id == recorded.Id)), It.IsAny<CancellationToken>()), Times.Once);
    }
}

