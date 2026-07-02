using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Services;

/// <summary>
/// End-to-end tests for the Tandem Source sync flow, ported from <c>tconnectsync</c> v3's
/// <c>tests/sync/tandemsource/test_e2e_process.py</c>. Drives the REAL
/// <see cref="TandemConnectorService"/> pipeline (device selection, windowing, JSON event decode,
/// all mappers) with HTTP mocked only at the transport layer; the pump-logs payload is the same
/// verbatim slice of real captured events used upstream (deviceAssignmentId redacted), and the
/// assertions are the Nocturne equivalents of upstream's golden Nightscout writes.
/// </summary>
public class TandemE2eSyncTests
{
    // The upstream fixture's pump lives at UTC-4 (America/New_York in May); every naive
    // pump-local timestamp below shifts by +4h to UTC.
    private const double TimezoneOffsetHours = -4;

    // One real pump for device selection (deviceAssignmentId redacted).
    private const string PumperJson = """
        {"pumps": [{"assignmentId": "e2e-device-assignment-id", "serialNumber": "1111111", "modelNumber": "0", "modelName": "Tandem Mobi™ System", "softwareVersion": "1.0", "maxDateOfEvents": "2026-05-18T10:19:55", "availableDataRange": {"start": "2026-05-14T00:01:00", "end": "2026-05-18T10:19:55"}}]}
        """;

    // Representative slice of real captured pump-log events (verbatim from the tconnectsync e2e
    // test): 2 basal (279), a full standard bolus (64/65/66/20 + 55/280), 2 CGM (399),
    // cartridge/cannula/tubing (33/61/63), a sleep start/stop pair (229), and a resume alarm
    // (5, which must NOT produce a system event).
    private const string PumpLogsJson = """
        {
        "events": [
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 441311, "pumpDateTime": "2026-05-14T00:01:00", "eventProperties": {"commandedRateSource": 3, "reservedA2": 3, "spareA3": 0, "commandedRate": 1279, "profileBasalRate": 1000, "algorithmRate": 1279, "tempRate": 65535}, "estimatedDateTime": "2026-05-14T00:01:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 441314, "pumpDateTime": "2026-05-14T00:01:31", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": -6, "algorithmState": 32, "rssi": -78, "currentGlucoseDisplayValue": 167, "egvTimeStamp": 579571288, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}, "estimatedDateTime": "2026-05-14T00:01:31Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 441336, "pumpDateTime": "2026-05-14T00:06:00", "eventProperties": {"commandedRateSource": 3, "reservedA2": 3, "spareA3": 0, "commandedRate": 1000, "profileBasalRate": 1000, "algorithmRate": 1000, "tempRate": 65535}, "estimatedDateTime": "2026-05-14T00:06:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 441339, "pumpDateTime": "2026-05-14T00:06:31", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": -13, "algorithmState": 32, "rssi": -79, "currentGlucoseDisplayValue": 149, "egvTimeStamp": 579571588, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}, "estimatedDateTime": "2026-05-14T00:06:31Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 64, "sequenceGroup": 0, "sequenceNumber": 442680, "pumpDateTime": "2026-05-14T11:02:20", "eventProperties": {"bolusId": 1583, "bolusType": 3, "correctionBolusIncluded": 1, "carbAmount": 20, "bg": 116, "iob": 0, "carbRatio": 0}, "estimatedDateTime": "2026-05-14T11:02:20Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 65, "sequenceGroup": 0, "sequenceNumber": 442681, "pumpDateTime": "2026-05-14T11:02:20", "eventProperties": {"bolusId": 1583, "options": 4, "standardPercent": 100, "duration": 0, "spareB6": 0, "isf": 0, "targetBg": 0, "userOverride": 0, "declinedCorrection": 0, "selectedIob": 1}, "estimatedDateTime": "2026-05-14T11:02:20Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 66, "sequenceGroup": 0, "sequenceNumber": 442682, "pumpDateTime": "2026-05-14T11:02:20", "eventProperties": {"bolusId": 1583, "spareA2": 0, "foodBolusSize": 3.33, "correctionBolusSize": 0.2, "totalBolusSize": 3.53}, "estimatedDateTime": "2026-05-14T11:02:20Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 55, "sequenceGroup": 0, "sequenceNumber": 442689, "pumpDateTime": "2026-05-14T11:02:35", "eventProperties": {"bolusId": 1583, "selectedIob": 1, "spareA3": 0, "iob": 0, "bolusSize": 3.53}, "estimatedDateTime": "2026-05-14T11:02:35Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 280, "sequenceGroup": 0, "sequenceNumber": 442690, "pumpDateTime": "2026-05-14T11:02:35", "eventProperties": {"bolusId": 1583, "bolusDeliveryStatus": 1, "bolusType": [0, 3, 4], "bolusSource": 8, "remoteId": 47, "requestedNow": 3530, "requestedLater": 0, "correction": 200, "extendedDurationRequested": 0, "deliveredTotal": 0}, "estimatedDateTime": "2026-05-14T11:02:35Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 280, "sequenceGroup": 0, "sequenceNumber": 442698, "pumpDateTime": "2026-05-14T11:04:18", "eventProperties": {"bolusId": 1583, "bolusDeliveryStatus": 0, "bolusType": [0, 3, 4], "bolusSource": 8, "remoteId": 47, "requestedNow": 3530, "requestedLater": 0, "correction": 200, "extendedDurationRequested": 0, "deliveredTotal": 3530}, "estimatedDateTime": "2026-05-14T11:04:18Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 20, "sequenceGroup": 0, "sequenceNumber": 442700, "pumpDateTime": "2026-05-14T11:04:18", "eventProperties": {"completionStatus": 3, "bolusId": 1583, "iob": 3.53, "insulinDelivered": 3.53, "insulinRequested": 3.53}, "estimatedDateTime": "2026-05-14T11:04:18Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 33, "sequenceGroup": 0, "sequenceNumber": 448073, "pumpDateTime": "2026-05-15T23:50:59", "eventProperties": {"insulinVolume": 180, "v2Volume": 0}, "estimatedDateTime": "2026-05-15T23:50:59Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 63, "sequenceGroup": 0, "sequenceNumber": 448074, "pumpDateTime": "2026-05-15T23:50:59", "eventProperties": {"primeSize": -1, "completionStatus": 3, "position": 547509}, "estimatedDateTime": "2026-05-15T23:50:59Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 5, "sequenceGroup": 0, "sequenceNumber": 448136, "pumpDateTime": "2026-05-16T00:06:00", "eventProperties": {"alarmId": 18, "faultLocatorData": 8311, "param1": 5228339, "param2": 0}, "estimatedDateTime": "2026-05-16T00:06:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 61, "sequenceGroup": 0, "sequenceNumber": 448176, "pumpDateTime": "2026-05-16T00:14:09", "eventProperties": {"primeSize": 0.3, "completionStatus": 3, "infusionSetType": 0}, "estimatedDateTime": "2026-05-16T00:14:09Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 456855, "pumpDateTime": "2026-05-18T10:16:00", "eventProperties": {"currentUserMode": 1, "previousUserMode": 0, "requestedAction": 1, "spareA3": 0, "sleepStartedByGui": 1, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}, "estimatedDateTime": "2026-05-18T10:16:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 456952, "pumpDateTime": "2026-05-18T10:19:55", "eventProperties": {"currentUserMode": 0, "previousUserMode": 1, "requestedAction": 2, "spareA3": 0, "sleepStartedByGui": 1, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}, "estimatedDateTime": "2026-05-18T10:19:55Z"}
            ],
        "clockChanges": []
        }
        """;

    private const string EmptyPumpLogsJson = """{"events": [], "clockChanges": []}""";

    [Fact]
    public async Task Full_sync_publishes_expected_records()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync();

        result.Success.Should().BeTrue();
        var pub = fixture.Publisher;

        // Temp basals: one 5-minute span per delivery event, the last extending to the pump's
        // maxDateOfEvents (upstream's "long duration" golden entry).
        pub.TempBasals.Should().HaveCount(2);
        var basal1 = pub.TempBasals[0];
        basal1.StartTimestamp.Should().Be(Utc(2026, 5, 14, 4, 1, 0));
        basal1.EndTimestamp.Should().Be(Utc(2026, 5, 14, 4, 6, 0));
        basal1.Rate.Should().Be(1.28);
        basal1.Origin.Should().Be(TempBasalOrigin.Algorithm);
        basal1.PumpRecordId.Should().Be("441311");
        basal1.UtcOffset.Should().Be(-240);
        var basal2 = pub.TempBasals[1];
        basal2.StartTimestamp.Should().Be(Utc(2026, 5, 14, 4, 6, 0));
        basal2.EndTimestamp.Should().Be(Utc(2026, 5, 18, 14, 19, 55));
        basal2.Rate.Should().Be(1.0);
        basal2.PumpRecordId.Should().Be("441336");

        // CGM readings are timestamped by their embedded EGV timestamp.
        pub.SensorGlucoses.Should().HaveCount(2);
        var sgv1 = pub.SensorGlucoses[0];
        sgv1.Mgdl.Should().Be(167);
        sgv1.Timestamp.Should().Be(Utc(2026, 5, 14, 4, 1, 28));
        sgv1.TrendRate.Should().BeApproximately(-0.6, 1e-9);
        sgv1.SyncIdentifier.Should().Be("tandem_cgm_441314");
        var sgv2 = pub.SensorGlucoses[1];
        sgv2.Mgdl.Should().Be(149);
        sgv2.Timestamp.Should().Be(Utc(2026, 5, 14, 4, 6, 28));
        sgv2.SyncIdentifier.Should().Be("tandem_cgm_441339");

        // The multi-message bolus reassembles into one bolus + carbs + calculation, all stamped
        // at the completion time and carrying every contributing sequence number.
        var bolus = pub.Boluses.Should().ContainSingle().Subject;
        bolus.Timestamp.Should().Be(Utc(2026, 5, 14, 15, 4, 18));
        bolus.Insulin.Should().Be(3.53);
        bolus.Programmed.Should().Be(3.53);
        bolus.Delivered.Should().Be(3.53);
        bolus.BolusType.Should().Be(Nocturne.Core.Models.V4.BolusType.Normal);
        bolus.Automatic.Should().BeFalse();
        bolus.SyncIdentifier.Should().Be("tandem_bolus_1583");
        bolus.PumpRecordId.Should().Be("442700,442680,442681,442682");
        bolus.AdditionalProperties.Should().ContainKey("notes").WhoseValue.Should().Be("BLE Standard Bolus");

        var carbs = pub.CarbIntakes.Should().ContainSingle().Subject;
        carbs.Carbs.Should().Be(20);
        carbs.Timestamp.Should().Be(Utc(2026, 5, 14, 15, 4, 18));
        carbs.SyncIdentifier.Should().Be("tandem_carb_1583");
        carbs.CorrelationId.Should().Be(bolus.CorrelationId);

        var calc = pub.BolusCalculations.Should().ContainSingle().Subject;
        calc.BloodGlucoseInput.Should().Be(116);
        calc.CarbInput.Should().Be(20);
        calc.InsulinOnBoard.Should().Be(0);
        calc.InsulinProgrammed.Should().Be(3.53);
        calc.InsulinRecommendation.Should().Be(3.53);
        calc.InsulinRecommendationForCarbs.Should().Be(3.33);

        // Fills: cartridge volume comes from insulinVolume (v2Volume is 0 here), the tubing
        // fill's primeSize of -1 means "not recorded" and is hidden, the cannula prime shows.
        pub.DeviceEvents.Should().HaveCount(3);
        var cartridge = pub.DeviceEvents.Single(e => e.EventType == DeviceEventType.ReservoirChange);
        cartridge.Notes.Should().Be("Cartridge Filled (180u filled)");
        cartridge.Timestamp.Should().Be(Utc(2026, 5, 16, 3, 50, 59));
        cartridge.SyncIdentifier.Should().Be("tandem_devent_448073");
        var tubing = pub.DeviceEvents.Single(e => e.EventType == DeviceEventType.TubePriming);
        tubing.Notes.Should().Be("Tubing Filled");
        var cannula = pub.DeviceEvents.Single(e => e.EventType == DeviceEventType.CannulaChange);
        cannula.Notes.Should().Be("Cannula Filled (0.3u primed)");
        cannula.Timestamp.Should().Be(Utc(2026, 5, 16, 4, 14, 9));

        // The manual sleep start/stop pair becomes one closed state span.
        var sleep = pub.StateSpans.Should().ContainSingle().Subject;
        sleep.Category.Should().Be(StateSpanCategory.Sleep);
        sleep.State.Should().Be("Sleep (Manual)");
        sleep.StartTimestamp.Should().Be(Utc(2026, 5, 18, 14, 16, 0));
        sleep.EndTimestamp.Should().Be(Utc(2026, 5, 18, 14, 19, 55));
        sleep.OriginalId.Should().Be("tandem_usermode_456855_456952");

        result.ItemsSynced.Should().Contain(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 2,
            [SyncDataType.Boluses] = 1,
            [SyncDataType.CarbIntake] = 1,
            [SyncDataType.BolusCalculations] = 1,
            [SyncDataType.TempBasals] = 2,
            [SyncDataType.DeviceEvents] = 3,
            [SyncDataType.StateSpans] = 1,
        });
    }

    [Fact]
    public async Task Requests_carry_bearer_token_and_source_origin()
    {
        var fixture = new Fixture();

        await fixture.RunAsync();

        var pumpLogs = fixture.Requests.Should()
            .ContainSingle(r => r.Path.Contains("/api/reports/bff/pump-logs/e2e-device-assignment-id")).Subject;
        pumpLogs.Path.Should().Contain("pumperId=pumper-1")
            .And.Contain("startDate=2026-05-14T00%3A00%3A00Z")
            .And.Contain("endDate=2026-05-18T23%3A59%3A59Z");
        pumpLogs.Authorization.Should().Be("Bearer fake-token");
        // The WAF requires same-origin Origin/Referer or it returns 403.
        pumpLogs.Origin.Should().Be("https://source.tandemdiabetes.com");
        pumpLogs.Referer.Should().Be("https://source.tandemdiabetes.com/");

        fixture.Requests.Should().Contain(r => r.Path == "/api/reports/bff/pumper/pumper-1");
    }

    [Fact]
    public async Task Resume_alarm_produces_no_system_event()
    {
        // The slice contains a resume alarm (code 5, alarmId 18 = RESUME_PUMP_ALARM); it must
        // not produce any published record.
        var fixture = new Fixture();

        await fixture.RunAsync();

        fixture.Publisher.SystemEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_window_publishes_nothing()
    {
        var fixture = new Fixture(pumpLogsJson: EmptyPumpLogsJson);

        var result = await fixture.RunAsync();

        result.Success.Should().BeTrue();
        fixture.Publisher.PublishedAnything.Should().BeFalse();
    }

    [Fact]
    public async Task Caught_up_sync_fetches_no_events()
    {
        // When the catch-up watermarks are already past the pump's newest event, the window is
        // empty and no pump-logs request is made (the Nocturne analog of upstream's dedup test —
        // server-side upserts handle per-record dedup, the window handles catch-up).
        var fixture = new Fixture(
            latestEntry: Utc(2026, 5, 19, 0, 0, 0),
            latestTreatment: Utc(2026, 5, 19, 0, 0, 0));

        var result = await fixture.RunAsync();

        result.Success.Should().BeTrue();
        fixture.Requests.Should().NotContain(r => r.Path.Contains("/pump-logs/"));
        fixture.Publisher.PublishedAnything.Should().BeFalse();
    }

    [Fact]
    public async Task Windows_over_28_days_are_paged_and_deduplicated()
    {
        // Widen the available range so the sync spans two pump-logs windows; the fake serves the
        // SAME events for both, and the (sequenceGroup, sequenceNumber) dedup must collapse them.
        var fixture = new Fixture(
            pumperJson: PumperJson.Replace("2026-05-14T00:01:00", "2026-04-01T00:00:00"),
            latestEntry: Utc(2026, 4, 10, 0, 0, 0),
            latestTreatment: Utc(2026, 4, 10, 0, 0, 0));

        var result = await fixture.RunAsync();

        result.Success.Should().BeTrue();
        fixture.Requests.Count(r => r.Path.Contains("/pump-logs/")).Should().Be(2);
        fixture.Publisher.SensorGlucoses.Should().HaveCount(2);
        fixture.Publisher.Boluses.Should().HaveCount(1);
        fixture.Publisher.TempBasals.Should().HaveCount(2);
    }

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private sealed record CapturedRequest(string Path, string? Authorization, string? Origin, string? Referer);

    private sealed class CapturingHandler(Dictionary<string, string> responses, List<CapturedRequest> requests)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(new CapturedRequest(
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Origin", out var origin) ? origin.First() : null,
                request.Headers.TryGetValues("Referer", out var referer) ? referer.First() : null));

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            foreach (var (key, json) in responses)
                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Skips the real OIDC dance, mirroring upstream's <c>_fake_login</c>.</summary>
    private sealed class FakeTandemAuthTokenProvider() : TandemAuthTokenProvider(
        new HttpClient(),
        new ConnectorTokenCache(),
        new ConnectorServerResolver<TandemConnectorConfiguration>(null, null, null),
        new FakeTenantAccessor(),
        NullLogger<TandemAuthTokenProvider>.Instance)
    {
        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)>
            AcquireTokenAsync(TandemConnectorConfiguration config, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>((
                "fake-token",
                DateTime.UtcNow.AddHours(1),
                new Dictionary<string, string> { [PumperIdKey] = "pumper-1", [AccountIdKey] = "account-1" }));

        private sealed class FakeTenantAccessor : ITenantAccessor
        {
            public bool IsResolved => true;
            public Guid TenantId => Guid.Empty;
            public TenantContext? Context => null;
            public void SetTenant(TenantContext context) { }
        }
    }

    /// <summary>Records every published record; the Nocturne analog of upstream's FakeNightscout.</summary>
    private sealed class RecordingPublisher(DateTime? latestEntry, DateTime? latestTreatment)
        : IConnectorPublisher, IGlucosePublisher, ITreatmentPublisher, IDevicePublisher, IMetadataPublisher
    {
        public List<SensorGlucose> SensorGlucoses { get; } = [];
        public List<Bolus> Boluses { get; } = [];
        public List<CarbIntake> CarbIntakes { get; } = [];
        public List<BolusCalculation> BolusCalculations { get; } = [];
        public List<TempBasal> TempBasals { get; } = [];
        public List<DeviceEvent> DeviceEvents { get; } = [];
        public List<DeviceStatus> DeviceStatuses { get; } = [];
        public List<StateSpan> StateSpans { get; } = [];
        public List<SystemEvent> SystemEvents { get; } = [];
        public List<Profile> Profiles { get; } = [];

        public bool PublishedAnything =>
            SensorGlucoses.Count + Boluses.Count + CarbIntakes.Count + BolusCalculations.Count +
            TempBasals.Count + DeviceEvents.Count + DeviceStatuses.Count + StateSpans.Count +
            SystemEvents.Count + Profiles.Count > 0;

        public bool IsAvailable => true;
        public IGlucosePublisher Glucose => this;
        public ITreatmentPublisher Treatments => this;
        public IDevicePublisher Device => this;
        public IMetadataPublisher Metadata => this;

        private static Task<bool> Record<T>(List<T> sink, IEnumerable<T> records)
        {
            sink.AddRange(records);
            return Task.FromResult(true);
        }

        public Task<bool> PublishEntriesAsync(IEnumerable<Entry> entries, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<bool> PublishSensorGlucoseAsync(IEnumerable<SensorGlucose> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(SensorGlucoses, records);
        public Task<DateTime?> GetLatestEntryTimestampAsync(string source, CancellationToken ct = default) =>
            Task.FromResult(latestEntry);
        public Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(string source, CancellationToken ct = default) =>
            Task.FromResult(latestEntry);

        public Task<bool> PublishTreatmentsAsync(IEnumerable<Treatment> treatments, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<bool> PublishBolusesAsync(IEnumerable<Bolus> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(Boluses, records);
        public Task<bool> PublishCarbIntakesAsync(IEnumerable<CarbIntake> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(CarbIntakes, records);
        public Task<bool> PublishBGChecksAsync(IEnumerable<BGCheck> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<bool> PublishBolusCalculationsAsync(IEnumerable<BolusCalculation> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(BolusCalculations, records);
        public Task<bool> PublishTempBasalsAsync(IEnumerable<TempBasal> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(TempBasals, records);
        public Task<bool> PublishBasalInjectionsAsync(IEnumerable<BasalInjection> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<DateTime?> GetLatestTreatmentTimestampAsync(string source, CancellationToken ct = default) =>
            Task.FromResult(latestTreatment);

        public Task<bool> PublishDeviceStatusAsync(IEnumerable<DeviceStatus> deviceStatuses, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(DeviceStatuses, deviceStatuses);
        public Task<bool> PublishDeviceEventsAsync(IEnumerable<DeviceEvent> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(DeviceEvents, records);
        public Task<DateTime?> GetLatestDeviceStatusTimestampAsync(string source, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);

        public Task<bool> PublishProfilesAsync(IEnumerable<Profile> profiles, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(Profiles, profiles);
        public Task<bool> PublishFoodAsync(IEnumerable<Food> foods, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(IEnumerable<ConnectorFoodEntryImport> entries, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConnectorFoodEntry>?>([]);
        public Task<bool> PublishActivityAsync(IEnumerable<Activity> activities, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<bool> PublishStateSpansAsync(IEnumerable<StateSpan> stateSpans, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(StateSpans, stateSpans);
        public Task<bool> PublishSystemEventsAsync(IEnumerable<SystemEvent> systemEvents, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Record(SystemEvents, systemEvents);
        public Task<bool> PublishNotesAsync(IEnumerable<Note> records, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<DateTime?> GetLatestActivityTimestampAsync(string source, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);
    }

    private sealed class Fixture
    {
        public RecordingPublisher Publisher { get; }
        public List<CapturedRequest> Requests { get; } = [];
        private readonly TandemConnectorService _service;
        private readonly TandemConnectorConfiguration _config;

        public Fixture(
            string pumperJson = PumperJson,
            string pumpLogsJson = PumpLogsJson,
            DateTime? latestEntry = null,
            DateTime? latestTreatment = null)
        {
            // Default watermarks put the catch-up start just before the pump's available range so
            // the sync window is deterministic (no dependence on "now" via the initial-sync floor).
            latestEntry ??= Utc(2026, 5, 13, 0, 0, 0);
            latestTreatment ??= Utc(2026, 5, 13, 0, 0, 0);

            _config = new TandemConnectorConfiguration
            {
                Email = "email@example.com",
                Password = "password",
                Region = "US",
                PumpSerialNumber = "11111111", // tconnectsync's "no serial chosen" sentinel
                TimezoneOffset = TimezoneOffsetHours,
            };

            Publisher = new RecordingPublisher(latestEntry, latestTreatment);
            var handler = new CapturingHandler(new Dictionary<string, string>
            {
                ["/api/reports/bff/pumper/"] = pumperJson,
                ["/api/reports/bff/pump-logs/"] = pumpLogsJson,
            }, Requests);

            _service = new TandemConnectorService(
                new HttpClient(handler),
                new ConnectorServerResolver<TandemConnectorConfiguration>(null, null, null),
                NullLogger<TandemConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                new FakeTandemAuthTokenProvider(),
                Publisher);
        }

        public Task<SyncResult> RunAsync() =>
            _service.SyncDataAsync(new SyncRequest(), _config, CancellationToken.None);
    }
}
