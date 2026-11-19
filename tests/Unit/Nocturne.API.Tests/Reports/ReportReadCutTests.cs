using FluentAssertions;
using Nocturne.Core.Contracts.Analytics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// What a report read keeps of its range: a layer folded onto a grid reads every row, so a slot's mean is of every
/// reading in it; a read that stops at a limit says so, and keeps the end the sentence speaks of.
/// </summary>
public class ReportReadCutTests
{
    private static readonly DateTimeOffset To = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    private static ReportDatasetRequest Request(
        ReportDatasetParameters parameters, IServiceProvider services, TimeSpan span, bool whole = false, params string[] scopes) =>
        new(parameters, ReportPanelKind.Chart, To - span, To, false, new HashSet<string>(scopes), services, new ReportReadLog(),
            Zone: TimeZoneInfo.Utc, Whole: whole);

    /// <summary>Every five minutes over the three hours before <see cref="To"/>.</summary>
    private static IEnumerable<DateTime> FiveMinutely() => Enumerable.Range(0, 36).Select(i => To.AddHours(-3).AddMinutes(5 * i).UtcDateTime);

    public static TheoryData<string> Folded => new()
    {
        GlucoseSeriesParameters.Name, SensorDetailParameters.Name, DeviceStatusParameters.Name, HeartRateParameters.Name, StepsParameters.Name,
        SleepBiometricsParameters.Name, BasalDeliveryParameters.Name,
    };

    [Theory]
    [MemberData(nameof(Folded))]
    public void A_layer_folded_onto_a_grid_reads_every_row_of_a_dataset_that_samples_otherwise(string name)
    {
        var dataset = ReportSpecValidatorTests.Registry.Find(name)!;
        var bindings = new ReportLayerBindings { X = "time", Y = [dataset.Fields.First(f => f.IsNumber).Name] };

        dataset.FoldsWhole.Should().BeTrue();
        ReportSpecRunner.LayerShape(dataset, bindings, folded: true).Whole.Should().BeTrue();
        ReportSpecRunner.LayerShape(dataset, bindings, folded: false).Whole.Should().BeFalse();
        dataset.ReadVariant(new ReportReadShape(Whole: true)).Should().NotBe(dataset.ReadVariant(ReportReadShape.Panel),
            "a whole read is cached and counted apart from a sampled one");
        ReportSpecRunner.Folds(30, ReportXKind.Time, ReportMark.Line).Should().BeTrue();
        ReportSpecRunner.Folds(30, ReportXKind.Time, ReportMark.Spans).Should().BeFalse();
        ReportSpecRunner.Folds(null, ReportXKind.Time, ReportMark.Line).Should().BeFalse();
    }

    [Fact]
    public async Task Basal_delivery_keeps_every_change_for_a_whole_read_so_the_last_value_of_a_slot_is_its_last_change()
    {
        var from = To.AddDays(-14).ToUnixTimeMilliseconds();
        var series = Enumerable.Range(0, 4032).Select(i => new BasalPoint
        {
            Timestamp = from + i * 300_000L, Rate = i % 2 == 0 ? 0.5 : 1.5, Origin = BasalDeliveryOrigin.Algorithm,
        }).ToList();
        var chart = new Mock<IChartDataService>();
        chart.Setup(c => c.GetBasalSeriesAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(series);
        var services = new ServiceCollection().AddSingleton(chart.Object).BuildServiceProvider();

        var sampled = await new BasalDeliveryDataset().RunAsync(Request(new BasalDeliveryParameters(), services, TimeSpan.FromDays(14)), CancellationToken.None);
        var whole = await new BasalDeliveryDataset().RunAsync(Request(new BasalDeliveryParameters(), services, TimeSpan.FromDays(14), whole: true), CancellationToken.None);

        sampled.Rows.Should().HaveCountLessThan(series.Count, "past the budget a sampled read keeps the first change of each slot");
        whole.Rows.Should().HaveCount(series.Count);
        whole.Resolution!.Downsampled.Should().BeFalse();
    }

    [Fact]
    public async Task Sensor_detail_keeps_every_reading_for_a_whole_read_and_one_a_slot_otherwise()
    {
        var glucose = new Mock<ISensorGlucoseRepository>();
        glucose.Setup(g => g.GetForAnalyticsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<GlucoseStreams>()))
            .ReturnsAsync([.. FiveMinutely().Select((at, i) => new SensorGlucose { Timestamp = at, Mgdl = 120, TrendRate = i % 2 == 0 ? 1 : -1 })]);
        var services = new ServiceCollection().AddSingleton(glucose.Object).BuildServiceProvider();

        var sampled = await new SensorDetailDataset().RunAsync(Request(new SensorDetailParameters(), services, TimeSpan.FromDays(90)), CancellationToken.None);
        var whole = await new SensorDetailDataset().RunAsync(Request(new SensorDetailParameters(), services, TimeSpan.FromDays(90), whole: true), CancellationToken.None);

        sampled.Rows.Should().HaveCountLessThan(5, "90 days in 2000 rows keeps one reading an hour");
        whole.Rows.Should().HaveCount(36);
        whole.Rows.Average(r => (double)r["trendRate"]!).Should().BeApproximately(0, 0.01, "the mean of every reading, not of the first of each slot");
    }

    [Fact]
    public async Task Device_status_keeps_every_report_for_a_whole_read()
    {
        var pump = new Mock<IPumpSnapshotRepository>();
        pump.Setup(p => p.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. FiveMinutely().Select((at, i) => new PumpSnapshot { Timestamp = at, Reservoir = 100 - i })]);
        var uploader = new Mock<IUploaderSnapshotRepository>();
        uploader.Setup(u => u.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var services = new ServiceCollection().AddSingleton(pump.Object).AddSingleton(uploader.Object).BuildServiceProvider();

        var sampled = await new DeviceStatusDataset().RunAsync(Request(new DeviceStatusParameters(), services, TimeSpan.FromDays(30), scopes: Scope.DevicesRead), CancellationToken.None);
        var whole = await new DeviceStatusDataset().RunAsync(Request(new DeviceStatusParameters(), services, TimeSpan.FromDays(30), true, Scope.DevicesRead), CancellationToken.None);

        sampled.Rows.Should().HaveCountLessThan(36);
        whole.Rows.Should().HaveCount(36);
    }

    [Fact]
    public async Task Heart_rate_keeps_every_minute_for_a_whole_read()
    {
        var heart = new Mock<IHeartRateService>();
        heart.Setup(h => h.GetHeartRateMinuteAveragesByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(0, 180).Select(i => new HeartRate { Timestamp = To.AddHours(-3).AddMinutes(i).UtcDateTime, Bpm = 60 + i % 2 })]);
        var services = new ServiceCollection().AddSingleton(heart.Object).BuildServiceProvider();

        var sampled = await new HeartRateDataset().RunAsync(Request(new HeartRateParameters(), services, TimeSpan.FromDays(90)), CancellationToken.None);
        var whole = await new HeartRateDataset().RunAsync(Request(new HeartRateParameters(), services, TimeSpan.FromDays(90), whole: true), CancellationToken.None);

        sampled.Rows.Should().HaveCountLessThan(5);
        whole.Rows.Should().HaveCount(180);
    }

    [Fact]
    public async Task Steps_are_added_up_in_their_finest_slot_for_a_whole_read()
    {
        var steps = new Mock<IStepCountService>();
        steps.Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. FiveMinutely().Select(at => new StepCount { Timestamp = at, Metric = 100 })]);
        var services = new ServiceCollection().AddSingleton(steps.Object).BuildServiceProvider();

        var whole = await new StepsDataset().RunAsync(Request(new StepsParameters(), services, TimeSpan.FromDays(90), whole: true), CancellationToken.None);

        whole.Resolution!.IntervalMinutes.Should().Be(15);
        whole.Rows.Should().HaveCount(12);
        whole.Rows.Sum(r => (double)r["steps"]!).Should().Be(3600);
    }

    private static Mock<ISleepSessionRepository> Sessions(int count, bool stages = false)
    {
        var start = To.AddDays(-30).UtcDateTime;
        var sessions = Enumerable.Range(0, count).Select(i => new SleepSession
        {
            StartTime = start.AddMinutes(30 * i), EndTime = start.AddMinutes(30 * i + 20), Type = SleepSessionType.Nap,
            Stages = stages
                ? [new SleepStageInterval { StartTime = start.AddMinutes(30 * i), EndTime = start.AddMinutes(30 * i + 20), Stage = SleepStageType.Light }]
                : null,
        }).ToList();
        var repository = new Mock<ISleepSessionRepository>();
        repository.Setup(s => s.GetSessionsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, SleepSessionType? _, SleepSource? _, int limit, int _, bool _, bool _, CancellationToken _) => sessions.Take(limit).ToList());
        repository.Setup(s => s.CountSessionsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(count);
        return repository;
    }

    [Fact]
    public async Task Sleep_figures_cut_at_their_session_limit_say_where()
    {
        var services = new ServiceCollection().AddSingleton(Sessions(1001).Object).BuildServiceProvider();

        var output = await new SleepNightsDataset().RunAsync(Request(new SleepNightsParameters(), services, TimeSpan.FromDays(31), scopes: Scope.SleepRead), CancellationToken.None);

        output.Rows.Should().HaveCount(1000);
        output.CutNote.Should().StartWith("Only the first 1000 sessions are shown, to the night of ");
        output.Resolution!.Downsampled.Should().BeTrue();
        output.Resolution.SourceRows.Should().Be(1001, "the sessions stored in the range, not the limit read past");
    }

    [Fact]
    public async Task Sleep_sessions_cut_at_their_limit_report_the_sessions_stored()
    {
        var services = new ServiceCollection().AddSingleton(Sessions(1500).Object).BuildServiceProvider();

        var output = await new SleepDataset().RunAsync(Request(new SleepParameters(), services, TimeSpan.FromDays(31)), CancellationToken.None);

        output.Rows.Should().HaveCount(1000);
        output.Resolution!.SourceRows.Should().Be(1500);
    }

    [Fact]
    public async Task Sleep_stages_cut_at_their_session_limit_say_so_though_the_stages_fit_the_budget()
    {
        var services = new ServiceCollection().AddSingleton(Sessions(401, stages: true).Object).BuildServiceProvider();

        var output = await new SleepStagesDataset().RunAsync(Request(new SleepStagesParameters(), services, TimeSpan.FromDays(31)), CancellationToken.None);

        output.Rows.Should().HaveCount(400);
        output.CutNote.Should().StartWith("Only the first 400 stages are shown, to ");
        output.Resolution!.Downsampled.Should().BeTrue();
    }

    [Fact]
    public async Task Sleep_stages_before_the_range_do_not_take_the_limit_of_those_in_it()
    {
        var from = To.AddDays(-1).UtcDateTime;
        var start = from.AddMinutes(-5000);
        var stages = Enumerable.Range(0, 5010).Select(i => new SleepStageInterval
        {
            StartTime = start.AddMinutes(i), EndTime = start.AddMinutes(i + 1), Stage = SleepStageType.Light,
        }).ToList();
        var repository = new Mock<ISleepSessionRepository>();
        repository.Setup(s => s.GetSessionsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SleepSession { StartTime = start, EndTime = start.AddMinutes(5010), Type = SleepSessionType.Overnight, Stages = stages }]);
        var services = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider();

        var output = await new SleepStagesDataset().RunAsync(Request(new SleepStagesParameters(), services, TimeSpan.FromDays(1)), CancellationToken.None);

        output.Rows.Should().HaveCount(10, "the ten stages in the range are read, not the first stages of the session");
        output.CutNote.Should().BeNull();
    }

    [Fact]
    public async Task Device_events_ask_for_the_drawn_kinds_only_so_others_do_not_take_the_limit()
    {
        var events = new Mock<IDeviceEventRepository>();
        IReadOnlyCollection<DeviceEventType>? asked = null;
        events.Setup(e => e.GetOfTypesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime _, DateTime _, IReadOnlyCollection<DeviceEventType> types, int _, CancellationToken _) => asked = types)
            .ReturnsAsync([]);
        var services = new ServiceCollection().AddSingleton(events.Object).BuildServiceProvider();

        await new DeviceEventsDataset().RunAsync(
            Request(new DeviceEventsParameters { Groups = [ReportDeviceEventGroup.Pump] }, services, TimeSpan.FromDays(7)), CancellationToken.None);

        asked.Should().NotBeNull().And.OnlyContain(t => DeviceEventsDataset.GroupOf(t) == ReportDeviceEventGroup.Pump)
            .And.Contain(DeviceEventType.PumpSuspend).And.NotContain(DeviceEventType.ProfileSwitch);
    }

    [Fact]
    public void A_lab_result_is_read_by_the_local_days_its_range_touches_and_not_the_day_the_range_ends_at()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var from = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(11));
        var to = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(11));

        LabHbA1cDataset.StoredDays(from, to, sydney).Should().Be((
            new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        LabHbA1cDataset.StoredDays(from, to.AddMinutes(1), sydney).End.Should().Be(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Dates_in_words_follow_the_readers_region_and_clock_and_stay_iso_without_them()
    {
        var at = new DateTimeOffset(2026, 11, 2, 0, 48, 0, TimeSpan.Zero);
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

        ReportFacts.Instant(at, new ReportClock(sydney)).Should().Be("2026-11-02 11:48 Australia/Sydney");
        ReportFacts.Instant(at, new ReportClock(sydney, Hour12: true, Region: "en-US")).Should().Be("Nov 2, 2026 11:48 AM Australia/Sydney");
        ReportFacts.Instant(at, new ReportClock(sydney, Hour12: false, Region: "en-AU")).Should().Be("2 Nov 2026 11:48 Australia/Sydney");
        new ReportClock(Region: "ja-JP").Date(new DateOnly(2026, 10, 20)).Should().Be("2026-10-20");
        new ReportClock(Region: "xx-NOPE").Date(new DateOnly(2026, 10, 20)).Should().Be("2026-10-20");
        ReportFacts.Window(at, at.AddDays(1)).Should().Be("2026-11-02 00:48 to 2026-11-03 00:48 UTC");
    }

    [Fact]
    public void A_derived_percent_is_written_to_one_decimal()
    {
        var percent = new ReportField("timeInRange", "Time in range", ReportFieldKind.Percent, "%", "");
        ReportGrid.Rounded(78.6812, percent).Should().Be(78.7);
    }

    [Fact]
    public void An_event_group_left_out_is_named_where_the_groups_read_had_no_event()
    {
        IReadOnlyList<ReportCohort> cohorts =
        [
            new(1, "Meals with Pizza", new ReportCohortOmission(ReportCohortOmission.ScopeReason, Scope.FoodRead)),
            new(2, "Lows"),
        ];

        EventResponseDataset.LeftOutSentence(cohorts, "in the last 14 days").Should().Be(
            "The group read had no event in the last 14 days. Not read: Meals with Pizza (it needs the food permission).");
    }

    [Fact]
    public void An_untitled_span_or_marker_layer_is_named_by_the_signals_it_draws()
    {
        var all = new HashSet<string>([Scope.DevicesRead, Scope.TherapyRead, Scope.TreatmentsRead, Scope.SleepRead]);
        new StateSpansDataset().LayerTitle(new StateSpansParameters { Kinds = [ReportSpanKind.PumpMode, ReportSpanKind.Override] }, all)
            .Should().Be("Pump mode, Override");
        new StateSpansDataset().LayerTitle(new StateSpansParameters(), all).Should().NotContain("State spans");
        new StateSpansDataset().LayerTitle(new StateSpansParameters { Kinds = [ReportSpanKind.PumpMode, ReportSpanKind.Profile] },
                new HashSet<string>([Scope.DevicesRead]))
            .Should().Be("Pump mode", "a kind the caller cannot read is not drawn, so it does not name the layer");
        new StateSpansDataset().LayerTitle(new StateSpansParameters { Kinds = [ReportSpanKind.Profile] }, new HashSet<string>()).Should().BeNull();
        var bindings = new ReportLayerBindings { X = "time", Y = ["insulin", "carbs"] };
        var fields = new TreatmentsDataset().Fields.Select(f => f.ResolveUnit(false)).ToList();
        ReportSpecRunner.MarkersTitle("Treatments", fields, bindings,
            [new ReportMarker(1, ReportMarkerKind.Bolus, 2), new ReportMarker(2, ReportMarkerKind.Carbs, 30)])
            .Should().Be("Bolus (U), Carbs (g)");
    }
}
