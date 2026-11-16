using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The signal datasets of the workspace's catalogue: device and system events, trackers, the stored state span kinds,
/// sleep detail, lab HbA1c and body weight, alerts, sensor detail, meter readings, compression lows and glucose episodes.
/// </summary>
public class SignalDatasetTests
{
    private static readonly DateTimeOffset To = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    private static ReportDatasetRequest Request(
        ReportDatasetParameters parameters, IServiceProvider services, ReportReadLog log, TimeSpan span, bool useMmol = false,
        params string[] scopes) =>
        new(parameters, ReportPanelKind.Chart, To - span, To, useMmol, new HashSet<string>(scopes), services, log, Zone: TimeZoneInfo.Utc);

    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    [Fact]
    public void Each_new_dataset_needs_the_scope_its_records_are_served_under()
    {
        new IReportDataset[]
        {
            new DeviceEventsDataset(), new SystemEventsDataset(), new TrackersDataset(), new SleepStagesDataset(), new SleepNightsDataset(),
            new SleepBiometricsDataset(), new LabHbA1cDataset(), new BodyWeightDataset(), new AlertExcursionsDataset(),
            new AlertInstancesDataset(), new DndWindowsDataset(), new SensorDetailDataset(), new MeterGlucoseDataset(),
            new CompressionLowsDataset(), new GlucoseEpisodesDataset(),
        }.ToDictionary(d => d.Name, d => d.RequiredScope).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["device_events"] = Scope.DevicesRead,
            ["system_events"] = Scope.DevicesRead,
            ["trackers"] = Scope.AlertsRead,
            ["sleep_stages"] = Scope.SleepRead,
            ["sleep_nights"] = Scope.SleepRead,
            ["sleep_biometrics"] = Scope.SleepRead,
            ["lab_hba1c"] = Scope.GlucoseRead,
            ["body_weight"] = Scope.TherapyRead,
            ["alert_excursions"] = Scope.AlertsRead,
            ["alert_instances"] = Scope.AlertsRead,
            ["dnd_windows"] = Scope.AlertsRead,
            ["sensor_detail"] = Scope.GlucoseRead,
            ["meter_glucose"] = Scope.GlucoseRead,
            ["compression_lows"] = Scope.GlucoseRead,
            ["glucose_episodes"] = Scope.GlucoseRead,
        });
    }

    [Fact]
    public void The_body_figures_of_sleep_need_the_heart_rate_scope_beside_sleep()
    {
        new SleepNightsDataset().Fields.Where(f => f.Scope == Scope.HeartRateRead).Select(f => f.Name)
            .Should().Equal("avgHeartRate", "minHeartRate", "hrv", "spo2", "breathingRate");
        new SleepBiometricsDataset().Fields.Where(f => f.Kind != ReportFieldKind.Time).Should().OnlyContain(f => f.Scope == Scope.HeartRateRead);
    }

    [Fact]
    public void A_lane_dataset_keeps_instants_in_the_range_and_cuts_spans_to_it()
    {
        var (from, to) = (1_000L, 2_000L);
        LaneDataset.Clip(
            [
                new("A", "before", 900, 900, ChartColor.Primary),
                new("A", "at the start", 1_000, 1_000, ChartColor.Primary),
                new("A", "at the end", 2_000, 2_000, ChartColor.Primary),
            ], from, to, instants: true)
            .Select(s => s.Label).Should().Equal("at the start");

        LaneDataset.Clip(
            [
                new("B", "over the start", 500, 1_500, ChartColor.Primary),
                new("B", "ended before", 500, 1_000, ChartColor.Primary),
                new("B", "still running", 1_800, long.MaxValue, ChartColor.Primary),
            ], from, to, instants: false)
            .Select(s => (s.Label, s.Start, s.End)).Should().Equal(("over the start", 1_000L, 1_500L), ("still running", 1_800L, 2_000L));
    }

    [Fact]
    public async Task Device_events_are_ticks_in_the_lanes_asked_for_and_say_when_nocturne_found_them()
    {
        var events = new Mock<IDeviceEventRepository>();
        DeviceEvent[] stored =
        [
            new DeviceEvent { Timestamp = To.AddDays(-2).UtcDateTime, EventType = DeviceEventType.SiteChange },
            new DeviceEvent { Timestamp = To.AddDays(-1).UtcDateTime, EventType = DeviceEventType.SensorStart, DataSource = DataSources.SensorSessionInference },
            new DeviceEvent { Timestamp = To.AddHours(-5).UtcDateTime, EventType = DeviceEventType.PumpSuspend },
            new DeviceEvent { Timestamp = To.AddHours(-4).UtcDateTime, EventType = DeviceEventType.ProfileSwitch },
        ];
        events.Setup(e => e.GetOfTypesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime _, DateTime _, IReadOnlyCollection<DeviceEventType> types, int limit, CancellationToken _) =>
                stored.Where(e => types.Contains(e.EventType)).Take(limit).ToList());
        var services = new ServiceCollection().AddSingleton(events.Object).BuildServiceProvider();
        var log = new ReportReadLog();

        var all = await new DeviceEventsDataset().RunAsync(Request(new DeviceEventsParameters(), services, log, TimeSpan.FromDays(7)), CancellationToken.None);
        var sensor = await new DeviceEventsDataset().RunAsync(
            Request(new DeviceEventsParameters { Groups = [ReportDeviceEventGroup.Sensor] }, services, new ReportReadLog(), TimeSpan.FromDays(7)),
            CancellationToken.None);

        all.Rows.Select(r => ((string)r["kind"]!, (string)r["label"]!)).Should().Equal(
            ("Site and insulin", "Site change"), ("Sensor", "Sensor start (found from a gap)"), ("Pump", "Pump suspend"));
        all.Rows.Should().OnlyContain(r => Equals(r["start"], r["end"]), "an event is one instant");
        all.Rows[0]["color"].Should().Be(ReportWire.Name(Nocturne.API.Helpers.ChartColorMapper.FromDeviceEvent(DeviceEventType.SiteChange)));
        all.Sentence.Should().Be("3 events in this period.");
        all.Summary.Should().Contain("Latest: Pump suspend at 2026-10-28 19:00 Etc/UTC.");
        sensor.Rows.Should().ContainSingle().Which["label"].Should().Be("Sensor start (found from a gap)");
        log.Datasets.Should().Equal("device_events");
        new DeviceEventsDataset().Validate(new DeviceEventsParameters { Groups = [ReportDeviceEventGroup.Pump, ReportDeviceEventGroup.Pump] })
            .Should().ContainSingle().Which.Field.Should().Be("groups");
    }

    [Fact]
    public async Task An_empty_range_of_device_alarms_says_so()
    {
        var system = new Mock<ISystemEventRepository>();
        system.Setup(s => s.GetInRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var log = new ReportReadLog();

        var output = await new SystemEventsDataset().RunAsync(
            Request(new SystemEventsParameters(), new ServiceCollection().AddSingleton(system.Object).BuildServiceProvider(), log, TimeSpan.FromDays(1)),
            CancellationToken.None);

        output.Rows.Should().BeEmpty();
        output.Sentence.Should().Be("No alarms in this period.");
        log.Datasets.Should().BeEmpty();
    }

    [Fact]
    public void A_device_alarm_is_named_by_its_type_and_what_the_device_said_in_the_lane_of_its_device()
    {
        SystemEventsDataset.LabelOf(new SystemEvent { EventType = SystemEventType.Alarm, Description = "Occlusion detected" })
            .Should().Be("Alarm: Occlusion detected");
        SystemEventsDataset.LabelOf(new SystemEvent { EventType = SystemEventType.Info, Code = "E12" }).Should().Be("Notice: E12");
        SystemEventsDataset.CategoryWords(SystemEventCategory.Connectivity).Should().Be("Connection");
    }

    [Fact]
    public async Task Sleep_stages_lie_in_lanes_awake_first_and_deep_last_whatever_order_they_came_in()
    {
        var start = To.AddHours(-8).UtcDateTime;
        var sleep = new Mock<ISleepSessionRepository>();
        sleep.Setup(s => s.GetSessionsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SleepSession
                {
                    StartTime = start, EndTime = start.AddHours(2),
                    Stages =
                    [
                        new SleepStageInterval { StartTime = start, EndTime = start.AddMinutes(30), Stage = SleepStageType.Deep },
                        new SleepStageInterval { StartTime = start.AddMinutes(30), EndTime = start.AddMinutes(60), Stage = SleepStageType.Light },
                        new SleepStageInterval { StartTime = start.AddMinutes(60), EndTime = start.AddMinutes(90), Stage = SleepStageType.Rem },
                        new SleepStageInterval { StartTime = start.AddMinutes(90), EndTime = start.AddMinutes(100), Stage = SleepStageType.AwakeInBed },
                        new SleepStageInterval { StartTime = start.AddMinutes(100), EndTime = start.AddMinutes(110), Stage = SleepStageType.Unknown },
                    ],
                },
            ]);

        var output = await new SleepStagesDataset().RunAsync(
            Request(new SleepStagesParameters(), new ServiceCollection().AddSingleton(sleep.Object).BuildServiceProvider(), new ReportReadLog(), TimeSpan.FromDays(1)),
            CancellationToken.None);

        output.Rows.Select(r => (string)r["kind"]!).Should().Equal("Awake", "REM", "Light", "Deep");
        output.Rows[^1]["color"].Should().Be(ReportWire.Name(ChartColor.Chart3));
    }

    [Fact]
    public void Glucose_episodes_are_the_statistics_episodes_in_a_lane_for_lows_and_one_for_highs()
    {
        var start = To.AddHours(-3).UtcDateTime;
        var readings = Enumerable.Range(0, 36).Select(i => new SensorGlucose
        {
            Timestamp = start.AddMinutes(i * 5),
            Mgdl = i is >= 6 and < 12 ? 60 : i is >= 20 and < 28 ? 260 : 120,
        }).ToList();

        var spans = GlucoseEpisodesDataset.Spans(GlucoseEpisodes.Find(readings, new GlycemicThresholds()), useMmol: true).ToList();

        spans.Select(s => (s.Lane, s.Label)).Should().Equal(("Lows", "Low, lowest 3.3 mmol/L"), ("Highs", "Very high, highest 14.4 mmol/L"));
        spans[0].Start.Should().Be(Ms(new DateTimeOffset(start.AddMinutes(30))));
        spans[0].End.Should().BeGreaterThan(spans[0].Start);
    }

    [Fact]
    public void Each_sleep_session_is_a_row_on_the_night_it_ended_and_its_body_figures_are_left_out_without_the_scope()
    {
        var sessions = new[]
        {
            new SleepSession
            {
                StartTime = To.AddHours(-30).UtcDateTime, EndTime = To.AddHours(-22).UtcDateTime, SleepScore = 81, Efficiency = 91.24f,
                SleepLatencyMs = 12 * 60_000, RestlessPeriods = 3, AvgHeartRate = 55.4f, MinHeartRate = 48f, AvgHrv = 41f, AvgSpo2 = 96.25f,
                AvgBreathRate = 14.1f,
            },
        };

        var held = SleepNightsDataset.Rows(sessions, TimeZoneInfo.Utc, body: true).Single();
        var withheld = SleepNightsDataset.Rows(sessions, TimeZoneInfo.Utc, body: false).Single();

        held["night"].Should().Be(Ms(To.AddDays(-1)));
        (held["score"], held["efficiency"], held["latency"], held["restless"]).Should().Be((81.0, 91.2, 12.0, 3.0));
        (held["avgHeartRate"], held["minHeartRate"], held["hrv"], held["spo2"], held["breathingRate"]).Should().Be((55.0, 48.0, 41.0, 96.2, 14.1));
        new[] { "avgHeartRate", "minHeartRate", "hrv", "spo2", "breathingRate" }.Should().OnlyContain(f => withheld[f] == null);
        withheld["score"].Should().Be(81.0);
    }

    [Fact]
    public async Task Sleep_body_readings_are_not_read_without_the_heart_rate_scope()
    {
        var output = await new SleepBiometricsDataset().RunAsync(
            Request(new SleepBiometricsParameters(), new ServiceCollection().BuildServiceProvider(), new ReportReadLog(), TimeSpan.FromDays(1),
                scopes: Scope.SleepRead),
            CancellationToken.None);

        output.Rows.Should().BeEmpty();
        output.Omitted.Select(o => (o.Field, o.Scope)).Should().Equal(
            ("heartRate", Scope.HeartRateRead), ("hrv", Scope.HeartRateRead), ("spo2", Scope.HeartRateRead), ("breathingRate", Scope.HeartRateRead));
    }

    [Fact]
    public void Sleep_body_readings_are_the_mean_of_each_slot_without_empty_readings()
    {
        var rows = SleepBiometricsDataset.Rows(
            [
                new(0, 60f, 40f, 97f, null),
                new(60_000, 62f, null, 95f, 14f),
                new(300_000, 50f, 0f, null, 15f),
            ], 0, 5);

        rows.Select(r => (r["time"], r["heartRate"], r["hrv"], r["spo2"], r["breathingRate"])).Should().Equal(
            (0L, (object?)61.0, (object?)40.0, (object?)96.0, (object?)14.0),
            (300_000L, (object?)50.0, null, null, (object?)15.0));
    }

    [Fact]
    public void A_rate_of_change_is_written_finer_than_a_glucose_value_in_either_unit()
    {
        SensorDetailDataset.Rate(2.0, useMmol: true).Should().Be(0.11);
        SensorDetailDataset.Rate(-1.25, useMmol: false).Should().Be(-1.2);
        SensorDetailDataset.Rate(null, useMmol: false).Should().BeNull();
        new SensorDetailDataset().Fields.Single(f => f.Name == "trendRate").ResolveUnit(true).Unit.Should().Be("mmol/L/min");
    }

    [Fact]
    public async Task Meter_readings_are_points_in_the_display_unit()
    {
        var meter = new Mock<IMeterGlucoseRepository>();
        meter.Setup(m => m.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MeterGlucose { Timestamp = To.AddHours(-1).UtcDateTime, Mgdl = 180 },
                new MeterGlucose { Timestamp = To.AddHours(-2).UtcDateTime, Mgdl = 90 },
            ]);
        var log = new ReportReadLog();

        var output = await new MeterGlucoseDataset().RunAsync(
            Request(new MeterGlucoseParameters(), new ServiceCollection().AddSingleton(meter.Object).BuildServiceProvider(), log, TimeSpan.FromDays(1), true),
            CancellationToken.None);

        output.Rows.Select(r => r["meterGlucose"]).Should().Equal(5.0, 10.0);
        output.Sentence.Should().Be("Meter readings: 2, mean 7.5 mmol/L, from 5.0 to 10.0 mmol/L.");
        log.Datasets.Should().Equal("meter_glucose");
    }

    [Fact]
    public void The_state_spans_the_chart_does_not_draw_are_read_under_their_categorys_scope_and_only_when_asked()
    {
        StateSpansDataset.ScopeOf(ReportSpanKind.DataExclusion).Should().Be(Scope.GlucoseRead);
        StateSpansDataset.ScopeOf(ReportSpanKind.PumpConnectivity).Should().Be(Scope.DevicesRead);
        StateSpansDataset.ScopeOf(ReportSpanKind.TemporaryTarget).Should().Be(Scope.TreatmentsRead);
        StateSpansDataset.ScopeOf(ReportSpanKind.Motion).Should().Be(Scope.TreatmentsRead);
        StateSpansDataset.ChartKinds.Should().Equal(
            ReportSpanKind.PumpMode, ReportSpanKind.Profile, ReportSpanKind.Override, ReportSpanKind.Activity, ReportSpanKind.TempBasal);

        var (from, to) = (Ms(To.AddDays(-1)), Ms(To));
        static DateTime At(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        var spans = StateSpansDataset.Stored(
            [
                new StateSpan { Category = StateSpanCategory.PumpConnectivity, State = "BluetoothOff", StartTimestamp = At(from - 60_000), EndTimestamp = At(from + 60_000) },
                new StateSpan { Category = StateSpanCategory.TemporaryTarget, State = "Active", StartTimestamp = At(from + 120_000) },
                new StateSpan { Category = StateSpanCategory.Motion, State = "Walking", StartTimestamp = At(from + 10_000), EndTimestamp = At(from + 20_000) },
            ],
            [ReportSpanKind.PumpConnectivity, ReportSpanKind.TemporaryTarget], from, to).ToList();

        spans.Select(s => (StateSpansDataset.Words(s.Kind), s.Label, s.Start, s.End)).Should().Equal(
            ("Pump connection", "Bluetooth off", from, from + 60_000),
            ("Temporary target", "Temporary target", from + 120_000, to));
        spans[0].Color.Should().Be(ChartColor.SystemEventWarning);
    }

    [Fact]
    public void A_lane_legend_and_its_tooltip_name_the_signal_and_its_unit()
    {
        ReportNames.Signal(new SleepNightsDataset().Fields.Single(f => f.Name == "avgHeartRate").Label).Should().Be("Heart rate in sleep");
        new IReportDataset[] { new DeviceEventsDataset(), new SleepStagesDataset(), new GlucoseEpisodesDataset() }
            .Should().OnlyContain(d => d.Marks.SequenceEqual(new[] { ReportMark.Spans }) && d.Fields.Count(f => f.Kind != ReportFieldKind.Time) == 3);
    }
}
