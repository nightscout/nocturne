using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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

    // An exercise start inside a window ending 2026-05-15, and — on the day after it — every
    // shape of record the padded day can carry: the delivery and stop events that close the
    // window's own span and exercise, and a closed basal pair, a whole bolus and a complete
    // exercise pair belonging to the day itself.
    private const string NextDayEvents = """
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 450100, "pumpDateTime": "2026-05-15T10:00:00", "eventProperties": {"currentUserMode": 2, "previousUserMode": 0, "requestedAction": 3, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}, "estimatedDateTime": "2026-05-15T10:00:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 450200, "pumpDateTime": "2026-05-16T09:00:00", "eventProperties": {"commandedRateSource": 1, "reservedA2": 3, "spareA3": 0, "commandedRate": 800, "profileBasalRate": 800, "algorithmRate": 800, "tempRate": 65535}, "estimatedDateTime": "2026-05-16T09:00:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 450210, "pumpDateTime": "2026-05-16T10:00:00", "eventProperties": {"commandedRateSource": 1, "reservedA2": 3, "spareA3": 0, "commandedRate": 900, "profileBasalRate": 900, "algorithmRate": 900, "tempRate": 65535}, "estimatedDateTime": "2026-05-16T10:00:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 64, "sequenceGroup": 0, "sequenceNumber": 450240, "pumpDateTime": "2026-05-16T09:15:00", "eventProperties": {"bolusId": 1600, "bolusType": 3, "correctionBolusIncluded": 0, "carbAmount": 45, "bg": 130, "iob": 0, "carbRatio": 0}, "estimatedDateTime": "2026-05-16T09:15:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 65, "sequenceGroup": 0, "sequenceNumber": 450241, "pumpDateTime": "2026-05-16T09:15:00", "eventProperties": {"bolusId": 1600, "options": 4, "standardPercent": 100, "duration": 0, "spareB6": 0, "isf": 0, "targetBg": 0, "userOverride": 0, "declinedCorrection": 0, "selectedIob": 1}, "estimatedDateTime": "2026-05-16T09:15:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 66, "sequenceGroup": 0, "sequenceNumber": 450242, "pumpDateTime": "2026-05-16T09:15:00", "eventProperties": {"bolusId": 1600, "spareA2": 0, "foodBolusSize": 5.0, "correctionBolusSize": 0.0, "totalBolusSize": 5.0}, "estimatedDateTime": "2026-05-16T09:15:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 20, "sequenceGroup": 0, "sequenceNumber": 450250, "pumpDateTime": "2026-05-16T09:15:00", "eventProperties": {"completionStatus": 3, "bolusId": 1600, "iob": 5.0, "insulinDelivered": 5.0, "insulinRequested": 5.0}, "estimatedDateTime": "2026-05-16T09:15:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 450300, "pumpDateTime": "2026-05-16T09:30:00", "eventProperties": {"currentUserMode": 0, "previousUserMode": 2, "requestedAction": 4, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}, "estimatedDateTime": "2026-05-16T09:30:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 450400, "pumpDateTime": "2026-05-16T11:00:00", "eventProperties": {"currentUserMode": 2, "previousUserMode": 0, "requestedAction": 3, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}, "estimatedDateTime": "2026-05-16T11:00:00Z"},
        {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 450410, "pumpDateTime": "2026-05-16T11:30:00", "eventProperties": {"currentUserMode": 0, "previousUserMode": 2, "requestedAction": 4, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}, "estimatedDateTime": "2026-05-16T11:30:00Z"},
        """;

    /// <summary>The fixture's events plus <see cref="NextDayEvents"/>.</summary>
    private static string WithNextDayEvents() =>
        PumpLogsJson.Replace("\"events\": [", "\"events\": [\n" + NextDayEvents);

    // One bolus whose request messages land on 2026-05-13 and whose completion lands on 2026-05-14:
    // reassembling it needs both days, and only the completion carries the delivered amount.
    private const string StraddlingBolusJson = """
        {
        "events": [
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 64, "sequenceGroup": 0, "sequenceNumber": 442680, "pumpDateTime": "2026-05-13T23:58:00", "eventProperties": {"bolusId": 1583, "bolusType": 3, "correctionBolusIncluded": 1, "carbAmount": 20, "bg": 116, "iob": 0, "carbRatio": 0}, "estimatedDateTime": "2026-05-13T23:58:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 65, "sequenceGroup": 0, "sequenceNumber": 442681, "pumpDateTime": "2026-05-13T23:58:00", "eventProperties": {"bolusId": 1583, "options": 4, "standardPercent": 100, "duration": 0, "spareB6": 0, "isf": 0, "targetBg": 0, "userOverride": 0, "declinedCorrection": 0, "selectedIob": 1}, "estimatedDateTime": "2026-05-13T23:58:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 66, "sequenceGroup": 0, "sequenceNumber": 442682, "pumpDateTime": "2026-05-13T23:58:00", "eventProperties": {"bolusId": 1583, "spareA2": 0, "foodBolusSize": 3.33, "correctionBolusSize": 0.2, "totalBolusSize": 3.53}, "estimatedDateTime": "2026-05-13T23:58:00Z"},
                {"deviceAssignmentId": "e2e-device-assignment-id", "eventCode": 20, "sequenceGroup": 0, "sequenceNumber": 442700, "pumpDateTime": "2026-05-14T00:02:00", "eventProperties": {"completionStatus": 3, "bolusId": 1583, "iob": 3.53, "insulinDelivered": 3.53, "insulinRequested": 3.53}, "estimatedDateTime": "2026-05-14T00:02:00Z"}
            ],
        "clockChanges": []
        }
        """;

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

        // The manual sleep start/stop pair produces nothing: sleep user modes no longer
        // map to state spans (sleep lives in the first-party sleep_sessions tables).
        pub.StateSpans.Should().BeEmpty();

        result.ItemsSynced.Should().Contain(new Dictionary<SyncDataType, int>
        {
            [SyncDataType.Glucose] = 2,
            [SyncDataType.Boluses] = 1,
            [SyncDataType.CarbIntake] = 1,
            [SyncDataType.BolusCalculations] = 1,
            [SyncDataType.TempBasals] = 2,
            [SyncDataType.DeviceEvents] = 3,
        });
        // Active but empty: an explicit zero says "checked, found nothing" where a missing key
        // would read as "never checked".
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(0);
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

    /// <summary>
    /// A narrowed request re-pulls one type; every other type the tenant has switched on stays
    /// untouched, however much of it the window carries.
    /// </summary>
    [Fact]
    public async Task Narrowed_request_syncs_only_the_requested_type()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync(new SyncRequest { DataTypes = [SyncDataType.Glucose] });

        // ItemsSynced carries a key per type the run looked at, so every other type must be absent.
        result.ItemsSynced.Keys.Should().Equal(SyncDataType.Glucose);
        fixture.Publisher.SensorGlucoses.Should().HaveCount(2);
        fixture.Publisher.Boluses.Should().BeEmpty();
        fixture.Publisher.TempBasals.Should().BeEmpty();
        fixture.Publisher.DeviceEvents.Should().BeEmpty();
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
            latestTreatment: Utc(2026, 4, 10, 0, 0, 0),
            servesEveryWindow: true);

        var result = await fixture.RunAsync();

        result.Success.Should().BeTrue();
        fixture.Requests.Count(r => r.Path.Contains("/pump-logs/")).Should().Be(2);
        fixture.Publisher.SensorGlucoses.Should().HaveCount(2);
        fixture.Publisher.Boluses.Should().HaveCount(1);
        fixture.Publisher.TempBasals.Should().HaveCount(2);
    }

    /// <summary>
    /// A bounded re-import asks the pump for a day either side of the window it was given, so the
    /// events that complete a record at each edge are in hand; the day-granular endpoint means that
    /// is one more chunk-day at each end, capped at the pump's newest event.
    /// </summary>
    [Fact]
    public async Task Explicit_request_window_fetches_a_day_either_side()
    {
        var fixture = new Fixture();

        await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 16, 0, 0, 0),
            To = Utc(2026, 5, 17, 0, 0, 0),
            DataTypes = [SyncDataType.Glucose],
        });

        var pumpLogs = fixture.Requests.Should()
            .ContainSingle(r => r.Path.Contains("/pump-logs/")).Subject;
        pumpLogs.Path.Should().Contain("startDate=2026-05-15T00%3A00%3A00Z")
            .And.Contain("endDate=2026-05-18T23%3A59%3A59Z");

        // The fixture's CGM readings are below even the padded window, and the pump serves only
        // what is inside the window asked for — so the bound is what decides this, not the payload.
        fixture.Publisher.SensorGlucoses.Should().BeEmpty();
    }

    /// <summary>
    /// The event that closes the window's last span sits in the day fetched past it, so the span is
    /// published with its true end — while the padded day's own span, which nothing closes, is not
    /// published at all: it belongs to the next window along.
    /// </summary>
    [Fact]
    public async Task Span_closed_in_the_padded_day_is_published_with_its_true_end()
    {
        // The pump's range opens before the window, so nothing here is clamped and the run has
        // nothing to report.
        var fixture = new Fixture(
            pumperJson: PumperJson.Replace("2026-05-14T00:01:00", "2026-05-12T00:00:00"),
            pumpLogsJson: WithNextDayEvents());

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 14, 0, 0, 0),
            To = Utc(2026, 5, 15, 0, 0, 0),
            DataTypes = [SyncDataType.TempBasals],
        });

        fixture.Publisher.TempBasals.Should().HaveCount(2);
        fixture.Publisher.TempBasals[^1].StartTimestamp.Should().Be(Utc(2026, 5, 14, 4, 6, 0));
        fixture.Publisher.TempBasals[^1].EndTimestamp.Should().Be(Utc(2026, 5, 16, 13, 0, 0));
        result.Message.Should().BeEmpty("every span inside the window was closed");
    }

    /// <summary>
    /// An exercise span is paired from its start and stop events, and a stop in the day fetched past
    /// the window closes it. Left unpaired it publishes open, under the id an unpaired span carries
    /// — a second row beside the closed one a full sync stored, rather than an upsert over it.
    /// </summary>
    [Fact]
    public async Task Exercise_span_stopped_in_the_padded_day_is_published_closed()
    {
        var fixture = new Fixture(pumpLogsJson: WithNextDayEvents());

        await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 14, 0, 0, 0),
            To = Utc(2026, 5, 15, 0, 0, 0),
            DataTypes = [SyncDataType.StateSpans],
        });

        var span = fixture.Publisher.StateSpans.Should().ContainSingle().Subject;
        span.StartTimestamp.Should().Be(Utc(2026, 5, 15, 14, 0, 0));
        span.EndTimestamp.Should().Be(Utc(2026, 5, 16, 13, 30, 0));
        span.OriginalId.Should().Be("tandem_usermode_450100_450300");
    }

    /// <summary>
    /// The days fetched either side of the window are there to complete its edge records, not to
    /// widen what it returns: a record of the padded day's own belongs to the window that covers
    /// it, and a caller that asked for one window is not handed three. Every record type whose
    /// correctness depends on an event across the edge is bounded, so each is pinned here — the
    /// padded day carries one of each, complete and publishable but for the bound.
    /// </summary>
    [Fact]
    public async Task Records_of_the_padded_day_are_left_to_the_window_that_covers_them()
    {
        var fixture = new Fixture(
            pumperJson: PumperJson.Replace("2026-05-14T00:01:00", "2026-05-12T00:00:00"),
            pumpLogsJson: WithNextDayEvents());

        await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 14, 0, 0, 0),
            To = Utc(2026, 5, 15, 0, 0, 0),
            DataTypes =
            [
                SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.BolusCalculations,
                SyncDataType.TempBasals, SyncDataType.StateSpans,
            ],
        });

        var publisher = fixture.Publisher;
        publisher.Boluses.Should().ContainSingle()
            .Which.SyncIdentifier.Should().Be("tandem_bolus_1583");
        publisher.CarbIntakes.Should().ContainSingle().Which.Carbs.Should().Be(20);
        publisher.BolusCalculations.Should().ContainSingle().Which.CarbInput.Should().Be(20);

        // The window's two deliveries, the second closed by the padded day's first; that one's own
        // span, closed by the delivery an hour after it, belongs to the next window.
        publisher.TempBasals.Should().HaveCount(2);
        publisher.TempBasals.Should().OnlyContain(record => record.StartTimestamp < Utc(2026, 5, 16, 0, 0, 0));

        publisher.StateSpans.Should().ContainSingle()
            .Which.StartTimestamp.Should().Be(Utc(2026, 5, 15, 14, 0, 0));
    }

    /// <summary>
    /// A bolus is reassembled from messages that can straddle the window's lower edge: the request
    /// messages carry the carbs and the calculation, the completion carries the delivery. Fetched
    /// alone, the completion still publishes — as a bare bolus, under the same stable id, over the
    /// complete record a full sync stored.
    /// </summary>
    [Fact]
    public async Task Bolus_straddling_the_lower_edge_is_published_complete()
    {
        var fixture = new Fixture(
            pumperJson: PumperJson.Replace("2026-05-14T00:01:00", "2026-05-12T00:00:00"),
            pumpLogsJson: StraddlingBolusJson);

        await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 14, 0, 0, 0),
            To = Utc(2026, 5, 15, 0, 0, 0),
            DataTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.BolusCalculations],
        });

        var bolus = fixture.Publisher.Boluses.Should().ContainSingle().Subject;
        bolus.Timestamp.Should().Be(Utc(2026, 5, 14, 4, 2, 0));
        fixture.Publisher.CarbIntakes.Should().ContainSingle().Which.Carbs.Should().Be(20);
        fixture.Publisher.BolusCalculations.Should().ContainSingle()
            .Which.InsulinRecommendationForCarbs.Should().Be(3.33);
    }

    /// <summary>
    /// A range naming no lower bound is the reset-cursor shape, and it asks for everything the pump
    /// still holds — the resume point is what it is resetting, so answering from it resets nothing.
    /// </summary>
    [Fact]
    public async Task Explicit_range_without_a_lower_bound_crawls_from_the_pumps_available_range()
    {
        var fixture = new Fixture(
            pumperJson: PumperJson.Replace("2026-05-14T00:01:00", "2026-04-01T00:00:00"));

        await fixture.RunAsync(new SyncRequest
        {
            From = null,
            To = Utc(2026, 5, 16, 0, 0, 0),
            DataTypes = [SyncDataType.Glucose],
        });

        fixture.Requests.First(r => r.Path.Contains("/pump-logs/")).Path
            .Should().Contain("startDate=2026-04-01T00%3A00%3A00Z");
    }

    /// <summary>
    /// A span the day fetched past the window still does not close — one running longer than that
    /// day — is not published, and the run says so. See
    /// <see cref="Nocturne.Connectors.Tandem.Mappers.TandemBasalSpans.UnclosedFrom"/>.
    /// </summary>
    [Fact]
    public async Task Explicit_upper_bound_publishes_only_spans_whose_end_was_fetched()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 14, 0, 0, 0),
            To = Utc(2026, 5, 15, 0, 0, 0),
            DataTypes = [SyncDataType.TempBasals],
        });

        // The window holds two delivery events; only the first is closed by one the fetch reached.
        var basal = fixture.Publisher.TempBasals.Should().ContainSingle().Subject;
        basal.StartTimestamp.Should().Be(Utc(2026, 5, 14, 4, 1, 0));
        basal.EndTimestamp.Should().Be(Utc(2026, 5, 14, 4, 6, 0));

        result.Message.Should().Contain("2026-05-14 04:06",
            "a span the run could not publish is the tenant's to chase, not silently absent");
    }

    /// <summary>
    /// The pump serves nothing before its available range begins, so a repair request reaching
    /// further back than that is clamped to it — and the run says so, rather than reporting a
    /// success that quietly covered a different window than the one asked for.
    /// </summary>
    [Fact]
    public async Task Request_below_the_pumps_available_range_is_clamped_and_reported()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 1, 1, 0, 0, 0),
            DataTypes = [SyncDataType.Glucose],
        });

        var pumpLogs = fixture.Requests.Should()
            .ContainSingle(r => r.Path.Contains("/pump-logs/")).Subject;
        pumpLogs.Path.Should().Contain("startDate=2026-05-14T00%3A00%3A00Z");
        result.Message.Should().Contain("2026-05-14 04:01",
            "a window the pump cannot serve is a clamp the tenant has to be told about");
    }

    /// <summary>
    /// On a background cycle the caller's bound is the glucose watermark, and it must not narrow
    /// the window past a data type that fell behind: the pump is crawled once for every type, so
    /// the earliest resume point across them is what the run owes.
    /// </summary>
    [Fact]
    public async Task Background_bound_does_not_narrow_a_type_that_fell_behind()
    {
        var fixture = new Fixture(
            latestEntry: Utc(2026, 5, 17, 0, 5, 0),
            latestTreatment: Utc(2026, 5, 16, 0, 0, 0));

        await fixture.RunAsync(new SyncRequest
        {
            From = Utc(2026, 5, 17, 0, 0, 0),
            DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses],
        });

        var pumpLogs = fixture.Requests.Should()
            .ContainSingle(r => r.Path.Contains("/pump-logs/")).Subject;
        pumpLogs.Path.Should().Contain("startDate=2026-05-15T00%3A00%3A00Z");
    }

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private sealed record CapturedRequest(string Path, string? Authorization, string? Origin, string? Referer);

    /// <param name="servesEveryWindow">
    ///     Whether the pump-logs payload is served whole however narrow the requested window is.
    ///     The real endpoint returns only the events inside it, so a test that asserts on what a
    ///     window published needs the default; the paging test wants the same events served twice.
    /// </param>
    private sealed class CapturingHandler(
        Dictionary<string, string> responses, List<CapturedRequest> requests,
        bool servesEveryWindow = false)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            requests.Add(new CapturedRequest(
                pathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Origin", out var origin) ? origin.First() : null,
                request.Headers.TryGetValues("Referer", out var referer) ? referer.First() : null));

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            foreach (var (key, json) in responses)
                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    var body = key.Contains("pump-logs", StringComparison.Ordinal) && !servesEveryWindow
                        ? WithinWindow(json, pathAndQuery)
                        : json;

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
                }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        /// <summary>
        /// The payload's events that fall inside the query's window, compared on the pump's own
        /// naive clock — the basis both the events' pumpDateTime and the query's dates are written on.
        /// </summary>
        private static string WithinWindow(string json, string pathAndQuery)
        {
            var from = QueryDate(pathAndQuery, "startDate");
            var to = QueryDate(pathAndQuery, "endDate");
            if (from is null || to is null)
                return json;

            var payload = JsonNode.Parse(json)!;
            var kept = new JsonArray();
            foreach (var node in payload["events"]!.AsArray().ToList())
            {
                var at = DateTime.Parse(
                    node!["pumpDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                if (at >= from && at <= to)
                    kept.Add(node.DeepClone());
            }

            payload["events"] = kept;
            return payload.ToJsonString();
        }

        private static DateTime? QueryDate(string pathAndQuery, string key)
        {
            var value = pathAndQuery
                .Split('?', 2).Last()
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0] == key)
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .FirstOrDefault();

            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
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
        public Task<int?> ReconcileConnectorFoodEntriesAsync(IEnumerable<string> presentExternalEntryIds, DateTimeOffset from, DateTimeOffset to, string source, WriteOrigin origin, CancellationToken ct = default) =>
            Task.FromResult<int?>(0);
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
        public Task<DateTime?> GetBackfillLowWaterMarkAsync(string source, string collection, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);
        public Task SetBackfillLowWaterMarkAsync(string source, string collection, DateTime? lowWaterMark, CancellationToken ct = default) =>
            Task.CompletedTask;
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
            DateTime? latestTreatment = null,
            bool servesEveryWindow = false)
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
            }, Requests, servesEveryWindow);

            _service = new TandemConnectorService(
                new HttpClient(handler),
                new ConnectorServerResolver<TandemConnectorConfiguration>(null, null, null),
                NullLogger<TandemConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                new FakeTandemAuthTokenProvider(),
                Publisher);
        }

        public Task<SyncResult> RunAsync(SyncRequest? request = null) =>
            _service.SyncDataAsync(request ?? new SyncRequest(), _config, CancellationToken.None);
    }
}
