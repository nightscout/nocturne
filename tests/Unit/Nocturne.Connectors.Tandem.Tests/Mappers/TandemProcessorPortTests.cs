using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Tandem.Tests.Mappers;

/// <summary>
/// Mapper tests ported from <c>tconnectsync</c> v3's per-processor suites
/// (<c>tests/sync/tandemsource/test_process_*.py</c>). Every fixture is a real captured pump-logs
/// JSON event (verbatim, deviceAssignmentId redacted) taken from upstream, and the expectations are
/// the Nocturne-record equivalents of upstream's golden Nightscout entries.
/// </summary>
public static class TandemProcessorPortTests
{
    /// <summary>Deserializes one pump-logs JSON event and decodes it (upstream's <c>Events([...])</c>).</summary>
    private static TandemPumpEvent Ev(string json) =>
        TandemEventDecoder.Decode(JsonSerializer.Deserialize<TandemPumpLogEvent>(json)!);

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    /// <summary>test_process_basal.py — real LID_BASAL_DELIVERY (279) events.</summary>
    public class Basal
    {
        // The fixture pump lives at UTC-4 (America/New_York in May).
        private static readonly TandemTimeResolver Time = new(-4);
        private readonly TandemBasalMapper _mapper = new(NullLogger.Instance, Time);

        // A short consecutive Algorithm-sourced run (rates 1279/1000/994 milliunits).
        private static readonly TandemPumpEvent[] Run =
        [
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 441311, "pumpDateTime": "2026-05-14T00:01:00", "eventProperties": {"commandedRateSource": 3, "reservedA2": 3, "spareA3": 0, "commandedRate": 1279, "profileBasalRate": 1000, "algorithmRate": 1279, "tempRate": 65535}}"""),
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 441336, "pumpDateTime": "2026-05-14T00:06:00", "eventProperties": {"commandedRateSource": 3, "reservedA2": 3, "spareA3": 0, "commandedRate": 1000, "profileBasalRate": 1000, "algorithmRate": 1000, "tempRate": 65535}}"""),
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 441346, "pumpDateTime": "2026-05-14T00:11:00", "eventProperties": {"commandedRateSource": 3, "reservedA2": 3, "spareA3": 0, "commandedRate": 994, "profileBasalRate": 1000, "algorithmRate": 994, "tempRate": 65535}}"""),
        ];

        // One real event per commandedRateSource value seen in the data.
        private static readonly TandemPumpEvent SrcSuspended =
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 447977, "pumpDateTime": "2026-05-15T23:40:01", "eventProperties": {"commandedRateSource": 0, "reservedA2": 0, "spareA3": 0, "commandedRate": 0, "profileBasalRate": 1000, "algorithmRate": 65535, "tempRate": 65535}}""");
        private static readonly TandemPumpEvent SrcProfile =
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 447435, "pumpDateTime": "2026-05-15T20:48:27", "eventProperties": {"commandedRateSource": 1, "reservedA2": 0, "spareA3": 0, "commandedRate": 1000, "profileBasalRate": 1000, "algorithmRate": 65535, "tempRate": 65535}}""");
        private static readonly TandemPumpEvent SrcTempRate =
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 449599, "pumpDateTime": "2026-05-16T11:16:07", "eventProperties": {"commandedRateSource": 2, "reservedA2": 0, "spareA3": 0, "commandedRate": 500, "profileBasalRate": 1000, "algorithmRate": 65535, "tempRate": 500}}""");
        private static readonly TandemPumpEvent SrcTempAndAlgo =
            Ev("""{"eventCode": 279, "sequenceGroup": 0, "sequenceNumber": 449321, "pumpDateTime": "2026-05-16T09:19:46", "eventProperties": {"commandedRateSource": 4, "reservedA2": 3, "spareA3": 0, "commandedRate": 0, "profileBasalRate": 1200, "algorithmRate": 0, "tempRate": 600}}""");

        [Fact]
        public void Consecutive_run_spans_each_delivery_to_the_next()
        {
            // Window ends 5 minutes after the last event, like upstream's RUN_TIME_END.
            var records = _mapper.Map(Run, Utc(2026, 5, 14, 4, 16, 0), ignoreZeroUnitBasal: false).Spans;

            records.Should().HaveCount(3);
            records[0].StartTimestamp.Should().Be(Utc(2026, 5, 14, 4, 1, 0));
            records[0].EndTimestamp.Should().Be(Utc(2026, 5, 14, 4, 6, 0));
            records[0].Rate.Should().Be(1.28);
            records[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
            records[0].PumpRecordId.Should().Be("441311");
            records[1].Rate.Should().Be(1.0);
            // Non-round scaling: 994 milliunits/hr -> 0.99 u/hr.
            records[2].Rate.Should().Be(0.99);
            records[2].EndTimestamp.Should().Be(Utc(2026, 5, 14, 4, 16, 0));
        }

        [Theory]
        [InlineData(0, 0.0, TempBasalOrigin.Suspended)]
        [InlineData(1, 1.0, TempBasalOrigin.Scheduled)]
        [InlineData(2, 0.5, TempBasalOrigin.Manual)]
        [InlineData(4, 0.0, TempBasalOrigin.Algorithm)]
        public void Commanded_rate_source_maps_to_origin(int source, double rate, TempBasalOrigin origin)
        {
            var ev = source switch
            {
                0 => SrcSuspended,
                1 => SrcProfile,
                2 => SrcTempRate,
                _ => SrcTempAndAlgo,
            };

            var records = _mapper.Map([ev], Utc(2026, 5, 17, 0, 0, 0), ignoreZeroUnitBasal: false).Spans;

            var basal = records.Should().ContainSingle().Subject;
            basal.Rate.Should().Be(rate);
            basal.Origin.Should().Be(origin);
        }

        [Fact]
        public void Zero_rate_events_are_skipped_when_ignore_zero_unit_basal()
        {
            var records = _mapper.Map(
                [SrcSuspended, SrcProfile], Utc(2026, 5, 17, 0, 0, 0), ignoreZeroUnitBasal: true).Spans;

            records.Should().ContainSingle().Which.PumpRecordId.Should().Be("447435");
        }

        [Fact]
        public void Last_event_extends_to_window_end_beyond_one_day()
        {
            // Upstream asserts a 25h gap yields a 1500-minute duration (total_seconds fix).
            var start = Utc(2026, 5, 16, 0, 48, 27); // SrcProfile at 20:48:27-04:00
            var windowEnd = start.AddHours(25);

            var records = _mapper.Map([SrcProfile], windowEnd, ignoreZeroUnitBasal: false).Spans;

            records.Should().ContainSingle().Which.EndTimestamp.Should().Be(windowEnd);
        }
    }

    /// <summary>test_process_bolus.py — complete real event groups per bolusId.</summary>
    public class Bolus
    {
        // Regular (Nov 2025) and extended (Dec 2025) fixtures are at UTC-5; canceled (May 2026) at UTC-4.
        private static readonly TandemTimeResolver Est = new(-5);
        private static readonly TandemTimeResolver Edt = new(-4);

        // bolusId 1868: a standard, completed bolus (the group also carries the
        // LID_BOLUS_ACTIVATED (55) and LID_BOLUS_DELIVERY (280) messages, which must be inert).
        private static readonly TandemPumpEvent[] Regular =
        [
            Ev("""{"eventCode": 64, "sequenceGroup": 0, "sequenceNumber": 450238, "pumpDateTime": "2025-11-27T12:45:32", "eventProperties": {"bolusId": 1868, "bolusType": 3, "correctionBolusIncluded": 1, "carbAmount": 15, "bg": 131, "iob": 0.06, "carbRatio": 0}}"""),
            Ev("""{"eventCode": 65, "sequenceGroup": 0, "sequenceNumber": 450239, "pumpDateTime": "2025-11-27T12:45:32", "eventProperties": {"bolusId": 1868, "options": 4, "standardPercent": 100, "duration": 0, "spareB6": 0, "isf": 0, "targetBg": 0, "userOverride": 0, "declinedCorrection": 0, "selectedIob": 1}}"""),
            Ev("""{"eventCode": 66, "sequenceGroup": 0, "sequenceNumber": 450240, "pumpDateTime": "2025-11-27T12:45:32", "eventProperties": {"bolusId": 1868, "spareA2": 0, "foodBolusSize": 2.5, "correctionBolusSize": 0.64, "totalBolusSize": 3.14}}"""),
            Ev("""{"eventCode": 55, "sequenceGroup": 0, "sequenceNumber": 450247, "pumpDateTime": "2025-11-27T12:45:47", "eventProperties": {"bolusId": 1868, "selectedIob": 1, "spareA3": 0, "iob": 0.059388563, "bolusSize": 3.1399999}}"""),
            Ev("""{"eventCode": 280, "sequenceGroup": 0, "sequenceNumber": 450248, "pumpDateTime": "2025-11-27T12:45:47", "eventProperties": {"bolusId": 1868, "bolusDeliveryStatus": 1, "bolusType": [0, 3, 4], "bolusSource": 8, "remoteId": 76, "requestedNow": 3140, "requestedLater": 0, "correction": 640, "extendedDurationRequested": 0, "deliveredTotal": 0}}"""),
            Ev("""{"eventCode": 20, "sequenceGroup": 0, "sequenceNumber": 450262, "pumpDateTime": "2025-11-27T12:47:18", "eventProperties": {"completionStatus": 3, "bolusId": 1868, "iob": 3.1993885, "insulinDelivered": 3.1399999, "insulinRequested": 3.1399999}}"""),
        ];

        // bolusId 2046: an extended (combo) bolus, 25% now / 75% over 180 min, whose extended
        // portion was aborted early (4.685u of the 6.247u requested).
        private static readonly TandemPumpEvent[] Extended =
        [
            Ev("""{"eventCode": 64, "sequenceGroup": 0, "sequenceNumber": 502747, "pumpDateTime": "2025-12-12T20:56:36", "eventProperties": {"bolusId": 2046, "bolusType": 3, "correctionBolusIncluded": 0, "carbAmount": 50, "bg": 111, "iob": 8.57, "carbRatio": 0}}"""),
            Ev("""{"eventCode": 65, "sequenceGroup": 0, "sequenceNumber": 502748, "pumpDateTime": "2025-12-12T20:56:36", "eventProperties": {"bolusId": 2046, "options": 5, "standardPercent": 25, "duration": 180, "spareB6": 0, "isf": 0, "targetBg": 0, "userOverride": 0, "declinedCorrection": 0, "selectedIob": 1}}"""),
            Ev("""{"eventCode": 66, "sequenceGroup": 0, "sequenceNumber": 502749, "pumpDateTime": "2025-12-12T20:56:36", "eventProperties": {"bolusId": 2046, "spareA2": 0, "foodBolusSize": 8.33, "correctionBolusSize": 0, "totalBolusSize": 8.330001}}"""),
            Ev("""{"eventCode": 20, "sequenceGroup": 0, "sequenceNumber": 502769, "pumpDateTime": "2025-12-12T20:57:51", "eventProperties": {"completionStatus": 3, "bolusId": 2046, "iob": 10.65024, "insulinDelivered": 2.0829997, "insulinRequested": 2.083}}"""),
            Ev("""{"eventCode": 59, "sequenceGroup": 0, "sequenceNumber": 502771, "pumpDateTime": "2025-12-12T20:57:51", "eventProperties": {"bolusId": 2046, "selectedIob": 1, "spareA3": 0, "iob": 10.65024, "bolexSize": 6.247}}"""),
            Ev("""{"eventCode": 21, "sequenceGroup": 0, "sequenceNumber": 503184, "pumpDateTime": "2025-12-12T23:14:27", "eventProperties": {"completionStatus": 0, "bolusId": 2046, "iob": 9.066635, "insulinDelivered": 4.6852484, "insulinRequested": 6.247}}"""),
        ];

        // bolusId 1644: a standard bolus canceled mid-delivery (0.04657u of 0.5u), with override.
        private static readonly TandemPumpEvent[] Canceled =
        [
            Ev("""{"eventCode": 64, "sequenceGroup": 0, "sequenceNumber": 456827, "pumpDateTime": "2026-05-18T10:15:21", "eventProperties": {"bolusId": 1644, "bolusType": 3, "correctionBolusIncluded": 0, "carbAmount": 0, "bg": 151, "iob": 1.181, "carbRatio": 0}}"""),
            Ev("""{"eventCode": 65, "sequenceGroup": 0, "sequenceNumber": 456828, "pumpDateTime": "2026-05-18T10:15:21", "eventProperties": {"bolusId": 1644, "options": 4, "standardPercent": 100, "duration": 0, "spareB6": 0, "isf": 0, "targetBg": 0, "userOverride": 1, "declinedCorrection": 0, "selectedIob": 1}}"""),
            Ev("""{"eventCode": 66, "sequenceGroup": 0, "sequenceNumber": 456829, "pumpDateTime": "2026-05-18T10:15:21", "eventProperties": {"bolusId": 1644, "spareA2": 0, "foodBolusSize": 0, "correctionBolusSize": 0, "totalBolusSize": 0.5}}"""),
            Ev("""{"eventCode": 20, "sequenceGroup": 0, "sequenceNumber": 456849, "pumpDateTime": "2026-05-18T10:15:39", "eventProperties": {"completionStatus": 0, "bolusId": 1644, "iob": 1.2275107, "insulinDelivered": 0.04657, "insulinRequested": 0.5}}"""),
        ];

        [Fact]
        public void Regular_bolus_reassembles_with_carbs_and_calculation()
        {
            var result = new TandemBolusMapper(NullLogger.Instance, Est).Map(Regular);

            var bolus = result.Boluses.Should().ContainSingle().Subject;
            bolus.Timestamp.Should().Be(Utc(2025, 11, 27, 17, 47, 18));
            bolus.Insulin.Should().Be(3.14);
            bolus.Programmed.Should().Be(3.14);
            bolus.BolusType.Should().Be(Nocturne.Core.Models.V4.BolusType.Normal);
            bolus.PumpRecordId.Should().Be("450262,450238,450239,450240");
            bolus.AdditionalProperties!["notes"].Should().Be("BLE Standard Bolus");

            result.CarbIntakes.Should().ContainSingle().Which.Carbs.Should().Be(15);
            var calc = result.BolusCalculations.Should().ContainSingle().Subject;
            calc.BloodGlucoseInput.Should().Be(131);
            calc.InsulinOnBoard.Should().Be(0.06);
            calc.InsulinRecommendation.Should().Be(3.14);
            calc.InsulinRecommendationForCarbs.Should().Be(2.5);
        }

        [Fact]
        public void Extended_bolus_totals_include_the_extended_portion()
        {
            // Upstream posts the extended portion separately (2.08 + 4.69 = 6.77u delivered);
            // Nocturne's single dual-wave record must carry the same totals, with the programmed
            // amount covering both portions (2.083 + 6.247 = 8.33u requested).
            var result = new TandemBolusMapper(NullLogger.Instance, Est).Map(Extended);

            var bolus = result.Boluses.Should().ContainSingle().Subject;
            bolus.Timestamp.Should().Be(Utc(2025, 12, 13, 1, 57, 51));
            bolus.Delivered.Should().Be(6.77);
            bolus.Programmed.Should().Be(8.33);
            bolus.BolusType.Should().Be(Nocturne.Core.Models.V4.BolusType.Dual);
            bolus.Duration.Should().Be(180);
            bolus.PumpRecordId.Should().Be("502769,502747,502748,502749,503184");
            bolus.AdditionalProperties!["notes"].Should().Be("BLE Extended Bolus");

            result.CarbIntakes.Should().ContainSingle().Which.Carbs.Should().Be(50);
            result.BolusCalculations.Should().ContainSingle().Which.BloodGlucoseInput.Should().Be(111);
        }

        [Fact]
        public void In_progress_extended_bolus_carries_only_the_initial_portion()
        {
            // A sync running before the extended portion finishes (no LID_BOLEX_COMPLETED yet)
            // publishes the initial amounts; the later sync re-publishes the same SyncIdentifier
            // with the full totals, so the record self-corrects via upsert.
            var withoutBolex = Extended.Where(e => e.Id != 21).ToArray();

            var result = new TandemBolusMapper(NullLogger.Instance, Est).Map(withoutBolex);

            var bolus = result.Boluses.Should().ContainSingle().Subject;
            bolus.Delivered.Should().Be(2.08);
            bolus.Programmed.Should().Be(2.08);
            bolus.SyncIdentifier.Should().Be("tandem_bolus_2046");
            // Still recognizably extended from the request's duration.
            bolus.BolusType.Should().Be(Nocturne.Core.Models.V4.BolusType.Dual);
        }

        [Fact]
        public void Canceled_bolus_records_delivered_not_requested()
        {
            var result = new TandemBolusMapper(NullLogger.Instance, Edt).Map(Canceled);

            var bolus = result.Boluses.Should().ContainSingle().Subject;
            bolus.Timestamp.Should().Be(Utc(2026, 5, 18, 14, 15, 39));
            bolus.Delivered.Should().Be(0.05);
            bolus.Programmed.Should().Be(0.5);
            bolus.AdditionalProperties!["notes"].Should().Be("BLE Standard Bolus (Override)");
            // No carbs were entered, so no carb intake is decomposed.
            result.CarbIntakes.Should().BeEmpty();
        }
    }

    /// <summary>test_process_cgm_reading.py — real G7 (399) and G6 (256) pump-logs JSON events.</summary>
    public class CgmReading
    {
        private static readonly TandemTimeResolver Edt = new(-4);
        private static readonly TandemTimeResolver Est = new(-5);

        private static readonly TandemPumpEvent[] G7 =
        [
            Ev("""{"eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 416999, "pumpDateTime": "2026-05-07T00:01:04", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": 8, "algorithmState": 32, "rssi": -59, "currentGlucoseDisplayValue": 249, "egvTimeStamp": 578966461, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}}"""),
            Ev("""{"eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 417212, "pumpDateTime": "2026-05-07T01:06:04", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": -12, "algorithmState": 32, "rssi": -57, "currentGlucoseDisplayValue": 193, "egvTimeStamp": 578970361, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}}"""),
            Ev("""{"eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 417300, "pumpDateTime": "2026-05-07T01:51:04", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": -10, "algorithmState": 32, "rssi": -51, "currentGlucoseDisplayValue": 118, "egvTimeStamp": 578973061, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}}"""),
            Ev("""{"eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 440616, "pumpDateTime": "2026-05-13T19:46:30", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": 5, "algorithmState": 32, "rssi": -67, "currentGlucoseDisplayValue": 321, "egvTimeStamp": 579555987, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}}"""),
            Ev("""{"eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 450283, "pumpDateTime": "2026-05-16T15:34:52", "eventProperties": {"glucoseValueStatus": 2, "cgmDataType": [0], "rate": -5, "algorithmState": 32, "rssi": -52, "currentGlucoseDisplayValue": 38, "egvTimeStamp": 579800089, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}}"""),
            Ev("""{"eventCode": 399, "sequenceGroup": 0, "sequenceNumber": 483972, "pumpDateTime": "2026-05-25T22:14:11", "eventProperties": {"glucoseValueStatus": 0, "cgmDataType": [0], "rate": 15, "algorithmState": 32, "rssi": -79, "currentGlucoseDisplayValue": 361, "egvTimeStamp": 580601648, "egvInfoBitmask": [0, 5, 6, 7, 8, 11, 12], "interval": 0, "reservedD15": 0}}"""),
        ];

        // Real G6 SpecialLow reading whose display value is 0 (early-2023 t:slim X2 capture).
        private static readonly TandemPumpEvent G6SpecialLowZero =
            Ev("""{"eventCode": 256, "sequenceGroup": 0, "sequenceNumber": 1113165, "pumpDateTime": "2023-01-19T20:30:24", "eventProperties": {"glucoseValueStatus": 2, "cgmDataType": [0], "rate": -11, "algorithmState": 6, "rssi": -79, "currentGlucoseDisplayValue": 0, "egvTimeStamp": 475014624, "egvInfoBitmask": [0, 5, 6, 7, 8, 11], "interval": 0, "reservedD15": 1}}""");

        [Fact]
        public void Reading_uses_egv_timestamp_not_store_time()
        {
            // pumpDateTime is 00:01:04 local but the embedded EGV timestamp is 00:01:01.
            var records = new TandemCgmMapper(NullLogger.Instance, Edt).Map([G7[0]]);

            var sgv = records.Should().ContainSingle().Subject;
            sgv.Mgdl.Should().Be(249);
            sgv.Timestamp.Should().Be(Utc(2026, 5, 7, 4, 1, 1));
            sgv.SyncIdentifier.Should().Be("tandem_cgm_416999");
        }

        [Fact]
        public void Diverse_glucose_range_reports_special_low_as_sentinel()
        {
            // The SpecialLow reading (raw display 38) is reported as the LOW sentinel 39.
            var records = new TandemCgmMapper(NullLogger.Instance, Edt).Map(G7);

            records.Select(r => r.Mgdl).Should().Equal(249, 193, 118, 321, 39, 361);
        }

        [Fact]
        public void Special_low_with_zero_display_value_still_reports_sentinel()
        {
            // Pre-sentinel behavior skipped this reading entirely; upstream (and the Tandem
            // Source frontend) report LOW.
            var records = new TandemCgmMapper(NullLogger.Instance, Est).Map([G6SpecialLowZero]);

            records.Should().ContainSingle().Which.Mgdl.Should().Be(39);
        }
    }

    /// <summary>test_process_alarm.py — real LID_ALARM_ACTIVATED (5) events.</summary>
    public class Alarm
    {
        private static readonly TandemTimeResolver Est = new(-5);
        private readonly TandemSystemEventMapper _mapper = new(NullLogger.Instance, Est);

        private static readonly TandemPumpEvent PumpReset =
            Ev("""{"eventCode": 5, "sequenceGroup": 0, "sequenceNumber": 2353636, "pumpDateTime": "2024-02-26T22:44:48", "eventProperties": {"alarmId": 3, "faultLocatorData": 8230, "param1": 0, "param2": 0}}""");
        private static readonly TandemPumpEvent EmptyCartridge =
            Ev("""{"eventCode": 5, "sequenceGroup": 0, "sequenceNumber": 1124751, "pumpDateTime": "2024-12-23T09:36:37", "eventProperties": {"alarmId": 8, "faultLocatorData": 8241, "param1": 103, "param2": 9.475377}}""");
        private static readonly TandemPumpEvent Resume =
            Ev("""{"eventCode": 5, "sequenceGroup": 0, "sequenceNumber": 448136, "pumpDateTime": "2026-05-16T00:06:00", "eventProperties": {"alarmId": 18, "faultLocatorData": 8311, "param1": 5228339, "param2": 0}}""");

        [Fact]
        public void Reportable_alarms_map_to_system_events()
        {
            var records = _mapper.Map([PumpReset, EmptyCartridge]);

            records.Should().HaveCount(2);
            var reset = records.Single(r => r.Code == "3");
            reset.Description.Should().Be("PUMP_RESET_ALARM");
            reset.EventType.Should().Be(SystemEventType.Alarm);
            reset.Mills.Should().Be(new DateTimeOffset(Utc(2024, 2, 27, 3, 44, 48)).ToUnixTimeMilliseconds());
            records.Single(r => r.Code == "8").Description.Should().Be("EMPTY_CARTRIDGE_ALARM");
        }

        [Fact]
        public void Resume_pump_alarm_is_skipped()
        {
            _mapper.Map([Resume]).Should().BeEmpty();
        }
    }

    /// <summary>test_process_cgm_alert.py — real LID_CGM_ALERT_ACTIVATED_DEX (369) events.</summary>
    public class CgmAlert
    {
        private static readonly TandemTimeResolver Edt = new(-4);
        private readonly TandemSystemEventMapper _mapper = new(NullLogger.Instance, Edt);

        private static readonly TandemPumpEvent High =
            Ev("""{"eventCode": 369, "sequenceGroup": 0, "sequenceNumber": 443773, "pumpDateTime": "2026-05-14T19:26:34", "eventProperties": {"dalertId": 2, "sensorType": 3, "spareA2": 0, "faultLocatorData": 8468, "param1": 210, "param2": 200}}""");
        private static readonly TandemPumpEvent Low =
            Ev("""{"eventCode": 369, "sequenceGroup": 0, "sequenceNumber": 443243, "pumpDateTime": "2026-05-14T15:41:34", "eventProperties": {"dalertId": 3, "sensorType": 3, "spareA2": 0, "faultLocatorData": 8467, "param1": 80, "param2": 80}}""");
        private static readonly TandemPumpEvent OutOfRange =
            Ev("""{"eventCode": 369, "sequenceGroup": 0, "sequenceNumber": 447441, "pumpDateTime": "2026-05-15T20:52:28", "eventProperties": {"dalertId": 14, "sensorType": 3, "spareA2": 0, "faultLocatorData": 8462, "param1": 25, "param2": 917}}""");
        private static readonly TandemPumpEvent Unmapped =
            Ev("""{"eventCode": 369, "sequenceGroup": 0, "sequenceNumber": 448285, "pumpDateTime": "2026-05-16T01:04:28", "eventProperties": {"dalertId": 31, "sensorType": 3, "spareA2": 0, "faultLocatorData": 10355, "param1": 43170, "param2": 452}}""");

        [Fact]
        public void Mapped_dexcom_alerts_become_cgm_warnings()
        {
            var records = _mapper.Map([High, Low]);

            records.Should().HaveCount(2);
            var low = records.Single(r => r.Code == "3");
            low.Description.Should().Be("Dexcom CGM Alert (CGM Low)");
            low.Category.Should().Be(SystemEventCategory.Cgm);
            low.EventType.Should().Be(SystemEventType.Warning);
            records.Single(r => r.Code == "2").Description.Should().Be("Dexcom CGM Alert (CGM High)");
        }

        [Fact]
        public void Out_of_range_and_unmapped_dexcom_alerts_are_skipped()
        {
            // CgmOutOfRange fires constantly and is explicitly skipped; an unmapped dalertId
            // resolves to no name and is skipped too.
            _mapper.Map([OutOfRange, Unmapped]).Should().BeEmpty();
        }
    }

    /// <summary>test_process_basal_suspension.py / test_process_basal_resume.py — real 11/12 events.</summary>
    public class SuspendResume
    {
        private static readonly TandemTimeResolver Edt = new(-4);
        private readonly TandemDeviceEventMapper _mapper = new(NullLogger.Instance, Edt);

        private static readonly TandemPumpEvent Suspend =
            Ev("""{"eventCode": 11, "sequenceGroup": 0, "sequenceNumber": 447946, "pumpDateTime": "2026-05-15T23:39:19", "eventProperties": {"preSuspendState": 106, "insulinAmount": 55, "suspendReason": 0, "rpaTimeout": 15}}""");
        private static readonly TandemPumpEvent Resume =
            Ev("""{"eventCode": 12, "sequenceGroup": 0, "sequenceNumber": 448183, "pumpDateTime": "2026-05-16T00:14:14", "eventProperties": {"preResumeState": 100, "insulinAmount": 180}}""");

        [Fact]
        public void Suspension_carries_the_suspend_reason()
        {
            var records = _mapper.Map([Suspend]);

            var suspend = records.Should().ContainSingle().Subject;
            suspend.EventType.Should().Be(DeviceEventType.PumpSuspend);
            suspend.Notes.Should().Be("User Aborted");
            suspend.Timestamp.Should().Be(Utc(2026, 5, 16, 3, 39, 19));
            suspend.SyncIdentifier.Should().Be("tandem_devent_447946");
        }

        [Fact]
        public void Resume_maps_to_pump_resume()
        {
            var records = _mapper.Map([Resume]);

            var resume = records.Should().ContainSingle().Subject;
            resume.EventType.Should().Be(DeviceEventType.PumpResume);
            resume.Timestamp.Should().Be(Utc(2026, 5, 16, 4, 14, 14));
        }
    }

    /// <summary>test_process_user_mode.py — real LID_AA_USER_MODE_CHANGE (229) exercise pair.</summary>
    public class UserMode
    {
        private static readonly TandemTimeResolver Edt = new(-4);

        private static readonly TandemPumpEvent ExerciseStart =
            Ev("""{"eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 456961, "pumpDateTime": "2026-05-18T10:20:04", "eventProperties": {"currentUserMode": 2, "previousUserMode": 0, "requestedAction": 3, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}}""");
        private static readonly TandemPumpEvent ExerciseStop =
            Ev("""{"eventCode": 229, "sequenceGroup": 0, "sequenceNumber": 456965, "pumpDateTime": "2026-05-18T10:20:15", "eventProperties": {"currentUserMode": 1, "previousUserMode": 2, "requestedAction": 4, "spareA3": 0, "sleepStartedByGui": 0, "activeSleepSchedule": [0], "spareB6": 0, "exerciseStoppedByTimer": 0, "exerciseChoice": 0, "exerciseTime": 0, "eatingSoonStoppedByTimer": 0}}""");

        [Fact]
        public void Exercise_start_stop_pairs_into_one_span()
        {
            var spans = new TandemUserModeMapper(NullLogger.Instance, Edt).Map([ExerciseStart, ExerciseStop]);

            var span = spans.Should().ContainSingle().Subject;
            span.Category.Should().Be(StateSpanCategory.Exercise);
            span.State.Should().Be("Exercise");
            span.StartTimestamp.Should().Be(Utc(2026, 5, 18, 14, 20, 4));
            span.EndTimestamp.Should().Be(Utc(2026, 5, 18, 14, 20, 15));
            span.OriginalId.Should().Be("tandem_usermode_456961_456965");
        }
    }
}
