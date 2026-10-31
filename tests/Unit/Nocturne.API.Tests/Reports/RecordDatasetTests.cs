using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The record datasets (treatments, basal_delivery, device_status, glucose_distribution,
/// daily_summary), glucose_statistics' insulin fields and glucose_series' markers: what each reads
/// for a panel, what it drops for a missing scope, and the summary it writes.
/// </summary>
public class RecordDatasetTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static readonly ReportSpecValidator Validator = new(ReportSpecValidatorTests.Registry, new FixedTime(Now));

    private static ReportSpec Spec(string json) =>
        Validator.Validate(JsonDocument.Parse(json).RootElement) is { IsValid: true } v
            ? v.Spec!
            : throw new InvalidOperationException(string.Join("; ", Validator.Validate(JsonDocument.Parse(json).RootElement).Errors));

    /// <summary>The services the datasets read, every repository a mock; glucose every five minutes over 15 days, cycling 50 to 340.</summary>
    internal sealed class Harness
    {
        public readonly Mock<ISensorGlucoseRepository> Glucose = new();
        public readonly Mock<ICanonicalGlucoseService> Canonical = new();
        public readonly Mock<ITherapySettingsResolver> Therapy = new();
        public readonly Mock<IBolusRepository> Boluses = new();
        public readonly Mock<ICarbIntakeRepository> Carbs = new();
        public readonly Mock<ITempBasalRepository> Temps = new();
        public readonly Mock<IBasalInjectionRepository> Injections = new();
        public readonly Mock<INoteRepository> Notes = new();
        public readonly Mock<IStateSpanService> Spans = new();
        public readonly Mock<IPumpSnapshotRepository> Pump = new();
        public readonly Mock<IUploaderSnapshotRepository> Uploader = new();
        public readonly Mock<IDeviceAgeService> Age = new();
        public readonly Mock<IChartDataService> Chart = new();

        public Harness(string? zone = "UTC")
        {
            var readings = Enumerable.Range(0, 15 * 288).Select(i => new SensorGlucose
            {
                Timestamp = Now.AddMinutes(-5 * i).UtcDateTime,
                Mgdl = 50 + i % 30 * 10,
            }).ToList();
            Glucose.Setup(g => g.GetForAnalyticsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
                .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, DateTime? _, Guid? _, CancellationToken _, Guid? _) =>
                    readings.Where(r => r.Timestamp >= from && r.Timestamp < to).OrderBy(r => r.Timestamp).ToList());
            Canonical.Setup(c => c.SelectAsync(It.IsAny<IReadOnlyList<SensorGlucose>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<SensorGlucose> r, CancellationToken _) => r);
            Therapy.Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(zone);
        }

        public (ReportSpecRunner Runner, ReportRunContext Context, ReportReadLog ReadLog) Arrange(params string[] scopes) =>
            Arrange(false, scopes);

        public (ReportSpecRunner Runner, ReportRunContext Context, ReportReadLog ReadLog) ArrangeClamped(params string[] scopes) =>
            Arrange(true, scopes);

        private (ReportSpecRunner Runner, ReportRunContext Context, ReportReadLog ReadLog) Arrange(bool clamped, string[] scopes)
        {
            var services = new ServiceCollection()
                .AddSingleton(Glucose.Object).AddSingleton(Canonical.Object).AddSingleton(Therapy.Object)
                .AddSingleton(Boluses.Object).AddSingleton(Carbs.Object).AddSingleton(Temps.Object).AddSingleton(Injections.Object)
                .AddSingleton(Notes.Object).AddSingleton(Spans.Object).AddSingleton(Pump.Object).AddSingleton(Uploader.Object)
                .AddSingleton(Age.Object).AddSingleton(Chart.Object)
                .AddSingleton(new TenantClockResolver(Therapy.Object, new CategoryReadContext(), NullLogger<TenantClockResolver>.Instance))
                .AddSingleton<IStatisticsService>(new StatisticsService(new FixedTime(Now)))
                .AddSingleton<TimeProvider>(new FixedTime(Now))
                .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
                .BuildServiceProvider();
            var readLog = new ReportReadLog();
            return (new ReportSpecRunner(ReportSpecValidatorTests.Registry, new FixedTime(Now)),
                new ReportRunContext(false, scopes.ToHashSet(), services, readLog, clamped), readLog);
        }

        public void WithBoluses(params (DateTimeOffset At, double Units)[] boluses) =>
            Boluses.Setup(b => b.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int _, int _, bool _, BolusKind? kind, DateTime? _, Guid? _, CancellationToken _) =>
                    kind == BolusKind.Algorithm ? [] : boluses.Select(b => new Bolus { Timestamp = b.At.UtcDateTime, Insulin = b.Units }).ToList());

        public void WithCarbs(params (DateTimeOffset At, double Grams)[] carbs) =>
            Carbs.Setup(c => c.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(carbs.Select(c => new CarbIntake { Timestamp = c.At.UtcDateTime, Carbs = c.Grams }).ToList());

        public void VerifyNoInsulinRead()
        {
            Boluses.Verify(b => b.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
            Carbs.Verify(c => c.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
            Temps.Verify(t => t.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            Injections.Verify(i => i.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>How many times the glucose readings were read from the repository.</summary>
        public int GlucoseReads => Glucose.Invocations.Count(i => i.Method.Name == nameof(ISensorGlucoseRepository.GetForAnalyticsAsync));
    }

    // ---- treatments ----

    [Fact]
    public async Task Treatments_read_only_the_kinds_asked_for_over_the_panel_range_and_filter_amounts_by_kind()
    {
        var harness = new Harness();
        harness.WithCarbs((Now.AddHours(-3), 12), (Now.AddHours(-2), 45));
        harness.Notes.Setup(n => n.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Note { Timestamp = Now.AddHours(-1).UtcDateTime, Text = "Walk" }]);
        var (runner, context, readLog) = harness.Arrange(Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 6 }, "panels": [
              { "dataset": { "name": "treatments", "types": ["carbs", "note"], "minCarbs": 30 }, "primitive": "table" } ] }
            """), context, CancellationToken.None);

        harness.Carbs.Verify(c => c.GetAsync(Now.AddHours(-6).UtcDateTime, Now.UtcDateTime, null, null, TreatmentsDataset.RecordLimit, 0, true,
            null, null, It.IsAny<CancellationToken>()), Times.Once);
        harness.Boluses.Verify(b => b.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        var panel = result.Panels.Single();
        panel.Rows.Select(r => (r["type"], r["carbs"], r["detail"])).Should().Equal(("carbs", (object?)45.0, (object?)null), ("note", null, "Walk"));
        panel.Summary.Should().Contain("Carb entries: 1, 45 g in all.").And.Contain("Notes: 1.").And.NotContain("Boluses");
        readLog.Datasets.Should().Equal("treatments");
        readLog.RecordCount.Should().Be(3, "the audit counts the records read, before the amount filter");
    }

    [Fact]
    public void The_treatments_summary_counts_and_totals_each_kind_and_never_lists_the_records()
    {
        var at = Now.AddHours(-4).ToUnixTimeMilliseconds();
        List<TreatmentsDataset.Record> records =
        [
            new(at, ReportTreatmentType.Carbs, Carbs: 45),
            new(at, ReportTreatmentType.Bolus, Insulin: 4.5, Automatic: false, Detail: "MealBolus"),
            new(at + 3_600_000, ReportTreatmentType.Bolus, Insulin: 0.3, Automatic: true, Detail: "Smb"),
            new(at + 3_900_000, ReportTreatmentType.TempBasal, Rate: 1.2, DurationMinutes: 30, Detail: "Algorithm"),
            new(at + 4_000_000, ReportTreatmentType.Note, Detail: "Felt low"),
        ];

        var summary = TreatmentsDataset.Summarise(records, Now.AddHours(-6), Now, TreatmentsDataset.Kinds(new TreatmentsParameters()),
            new StatisticsService(new FixedTime(Now)), records.Count);

        summary.Should().Be(
            "Treatments from 2026-10-29 06:00 to 2026-10-29 12:00 UTC.\n"
            + "Boluses: 2, 4.8 U in all (1 given by the algorithm). Largest 4.5 U at 2026-10-29 08:00. Latest 0.3 U at 2026-10-29 09:00.\n"
            + "Carb entries: 1, 45 g in all. Largest 45 g at 2026-10-29 08:00. Latest 45 g at 2026-10-29 08:00.\n"
            + "Temp basals: 1. Latest at 2026-10-29 09:05.\n"
            + "Overrides: 0.\n"
            + "Notes: 1. Latest at 2026-10-29 09:06.\n"
            + "Long-acting injections: 0.");
        summary.Should().NotContain("Felt low", "a note's text is a row, not part of the summary");
    }

    [Fact]
    public async Task A_range_with_more_records_than_the_budget_keeps_the_latest_and_says_so()
    {
        var harness = new Harness();
        harness.WithCarbs([.. Enumerable.Range(0, 700).Select(i => (Now.AddMinutes(-i), 10.0))]);
        var (runner, context, _) = harness.Arrange(Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 24 }, "panels": [ { "dataset": { "name": "treatments", "types": ["carbs"] }, "primitive": "table" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Should().HaveCount(TreatmentsDataset.Budget);
        panel.Rows[^1]["time"].Should().Be(Now.ToUnixTimeMilliseconds(), "the latest are kept");
        panel.Resolution!.Downsampled.Should().BeTrue();
        panel.Summary.Should().Contain("Carb entries: 700").And.Contain($"The rows hold the latest {TreatmentsDataset.Budget} of 700 records");
    }

    [Fact]
    public void A_treatments_time_series_draws_its_amounts_as_the_dashboards_markers()
    {
        var panel = Spec("""{ "version": 1, "panels": [ { "dataset": { "name": "treatments" }, "primitive": "time_series" } ] }""").Panels[0];

        panel.Bindings!.Y.Should().Equal("insulin");
        var dataset = ReportSpecValidatorTests.Registry.Find("treatments")!;
        dataset.Fields.Single(f => f.Name == "insulin").Marker.Should().Be(ReportMarkerKind.Bolus);
        dataset.Fields.Single(f => f.Name == "carbs").Marker.Should().Be(ReportMarkerKind.Carbs);
    }

    [Fact]
    public async Task An_injection_is_not_a_bolus_and_the_markers_carry_the_algorithms_flag()
    {
        var harness = new Harness();
        harness.Boluses.Setup(b => b.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Bolus { Timestamp = Now.AddHours(-3).UtcDateTime, Insulin = 4.5 },
                new Bolus { Timestamp = Now.AddHours(-2).UtcDateTime, Insulin = 0.3, Automatic = true },
            ]);
        harness.Injections.Setup(i => i.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BasalInjection { Timestamp = Now.AddHours(-1).UtcDateTime, Units = 20 }]);
        var (runner, context, _) = harness.Arrange(Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 6 }, "panels": [
              { "id": "line", "dataset": { "name": "treatments" }, "primitive": "time_series" },
              { "id": "rows", "dataset": { "name": "treatments" }, "primitive": "table" } ] }
            """), context, CancellationToken.None);

        var line = result.Panels[0];
        line.Bindings.Y.Should().Equal("insulin");
        line.Markers.Select(m => (m.Kind, m.Amount, m.Automatic)).Should().Equal(
            (ReportMarkerKind.Bolus, 4.5, false), (ReportMarkerKind.Bolus, 0.3, true));
        var injection = result.Panels[1].Rows.Single(r => (string?)r["type"] == "basal_injection");
        injection["insulin"].Should().BeNull("a 20 U long-acting dose is not a bolus");
        injection["injectionUnits"].Should().Be(20.0);
    }

    [Fact]
    public void The_treatments_summary_says_when_a_kind_reached_the_read_limit()
    {
        var records = new List<TreatmentsDataset.Record> { new(Now.ToUnixTimeMilliseconds(), ReportTreatmentType.Bolus, Insulin: 0.2, Automatic: true) };

        TreatmentsDataset.Summarise(records, Now.AddDays(-14), Now, TreatmentsDataset.Kinds(new TreatmentsParameters()),
                new StatisticsService(new FixedTime(Now)), 1, new HashSet<ReportTreatmentType> { ReportTreatmentType.Bolus })
            .Should().Contain($"Only the latest {TreatmentsDataset.RecordLimit} records of each kind are read, and this period has more of these kinds: bolus.");
    }

    [Fact]
    public async Task Without_treatments_read_the_treatments_panel_is_not_read()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "treatments" }, "primitive": "table" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Status.Should().Be(ReportPanelStatus.NotPermitted);
        panel.Summary.Should().Contain("'treatments.read'");
        harness.VerifyNoInsulinRead();
        readLog.Datasets.Should().BeEmpty();
    }

    // ---- basal_delivery ----

    private static List<BasalPoint> Basal(long from) =>
    [
        new() { Timestamp = from, Rate = 0.8, ScheduledRate = 0.8, Origin = BasalDeliveryOrigin.Scheduled },
        new() { Timestamp = from + 1_800_000, Rate = 1.5, ScheduledRate = 0.8, Origin = BasalDeliveryOrigin.Algorithm },
        new() { Timestamp = from + 3_600_000, Rate = 0, ScheduledRate = 0.8, Origin = BasalDeliveryOrigin.Algorithm },
    ];

    [Fact]
    public async Task Basal_delivery_reads_the_dashboards_basal_series_over_the_panel_range()
    {
        var harness = new Harness();
        var from = Now.AddHours(-6).ToUnixTimeMilliseconds();
        harness.Chart.Setup(c => c.GetBasalSeriesAsync(from, Now.ToUnixTimeMilliseconds(), It.IsAny<CancellationToken>())).ReturnsAsync(Basal(from));
        var (runner, context, readLog) = harness.Arrange(Scope.TreatmentsRead, Scope.TherapyRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 6 }, "panels": [ { "dataset": { "name": "basal_delivery" }, "primitive": "time_series" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Fields.Select(f => f.Name).Should().Equal("time", "delivered", "scheduled");
        panel.Fields.Where(f => f.Name != "time").Should().OnlyContain(f => f.Step);
        panel.Rows.Select(r => r["delivered"]).Should().Equal(0.8, 1.5, 0.0);
        panel.Summary.Should().Be(
            "Basal delivery from 2026-10-29 06:00 to 2026-10-29 12:00 UTC.\n"
            + "Rate changes: 3. Delivered rate from 0 to 1.5 U/h.\n"
            + "Scheduled rate from 0.8 to 0.8 U/h.\n"
            + "Changes to a rate above the schedule: 1. Below it: 1.\n"
            + "Changes by origin: Algorithm 2, Scheduled 1.");
        readLog.Datasets.Should().Equal("treatments", "profiles");
    }

    [Fact]
    public async Task Without_therapy_read_basal_delivery_drops_the_scheduled_rate_and_says_so()
    {
        var harness = new Harness();
        var from = Now.AddHours(-6).ToUnixTimeMilliseconds();
        harness.Chart.Setup(c => c.GetBasalSeriesAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(Basal(from));
        var (runner, context, readLog) = harness.Arrange(Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 6 }, "panels": [ { "dataset": { "name": "basal_delivery" }, "primitive": "time_series" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Fields.Select(f => f.Name).Should().Equal("time", "delivered");
        panel.Rows.Should().OnlyContain(r => !r.ContainsKey("scheduled"));
        panel.Omitted.Should().ContainSingle().Which.Should().Be(new ReportOmittedField("scheduled", Scope.TherapyRead));
        panel.Summary.Should().NotContain("Scheduled rate").And.Contain("The schedule on its own is omitted: it needs the therapy.read scope");
        readLog.Datasets.Should().Equal(["treatments", "profiles"], "the series is built on the profile whatever the token holds");
    }

    [Fact]
    public async Task Panels_over_the_same_range_read_the_glucose_once_and_audit_it_once()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [
              { "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles" },
              { "dataset": { "name": "glucose_distribution_by_hour" }, "primitive": "range_bands" },
              { "dataset": { "name": "glucose_distribution" }, "primitive": "bar" } ] }
            """), context, CancellationToken.None);

        harness.GlucoseReads.Should().Be(1);
        readLog.Datasets.Should().Equal("glucose");
        readLog.RecordCount.Should().Be(14 * 288, "fourteen days of five-minute readings, counted once");
    }

    [Fact]
    public async Task A_panel_at_its_datasets_longest_range_says_so()
    {
        var (runner, context, _) = new Harness().Arrange(Scope.GlucoseRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "panels": [
              { "id": "long", "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles", "range": { "hours": 2160 } },
              { "id": "short", "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles", "range": { "hours": 24 } } ] }
            """), context, CancellationToken.None);

        var full = result.Panels[0];
        full.AtRangeLimit.Should().BeTrue();
        full.RangeLimit.Should().Be("This panel covers at most 90 days.");
        result.Panels[1].AtRangeLimit.Should().BeFalse();
        result.Panels[1].RangeLimit.Should().BeNull();
    }

    [Fact]
    public async Task Every_panel_carries_at_most_six_facts_one_sentence_and_its_range_line()
    {
        var (runner, context, _) = new Harness().Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [
              { "id": "stats", "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles" },
              { "id": "hours", "dataset": { "name": "glucose_distribution_by_hour" }, "primitive": "range_bands" },
              { "id": "bands", "dataset": { "name": "glucose_distribution" }, "primitive": "bar" },
              { "id": "days", "dataset": { "name": "daily_summary" }, "primitive": "table" },
              { "id": "compare", "dataset": { "name": "period_comparison", "preset": "last7-prior7" }, "primitive": "comparison" } ] }
            """), context, CancellationToken.None);

        result.Panels.Should().OnlyContain(p => p.Facts.Count > 0 && p.Facts.Count <= ReportFacts.Max);
        result.Panels.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Sentence) && !p.Sentence!.Contains('\n'));
        result.Panels.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Context));
        var stats = result.Panels[0];
        stats.Facts.Select(f => f.Label).Should().Equal("In range", "Below range", "Above range", "Mean", "GMI", "CV");
        stats.Facts[0].Unit.Should().Be("%");
        stats.Facts[0].Note.Should().Be("70-180 mg/dL");
        stats.Context.Should().Be("2026-10-15 12:00 to 2026-10-29 12:00 UTC. 4032 readings.");
        result.Panels[2].Facts.Select(f => f.Label).Should().Equal("Very low", "Low", "In range", "High", "Very high");
        result.Panels[4].Facts[0].Should().Match<ReportFact>(f => f.Label == "In range" && f.Value.Contains(" to ") && f.Unit == "%");
    }

    [Fact]
    public async Task A_panel_that_was_not_read_says_why_in_its_sentence()
    {
        var (runner, context, _) = new Harness().Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Facts.Should().BeEmpty();
        panel.Sentence.Should().Be(panel.Summary).And.Contain("devices.read");
    }

    // ---- device_status ----

    [Fact]
    public async Task Device_status_merges_pump_and_uploader_reports_and_its_tiles_add_the_sensor_age()
    {
        var harness = new Harness();
        harness.Pump.Setup(p => p.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PumpSnapshot { Timestamp = Now.AddHours(-2).UtcDateTime, Reservoir = 120, BatteryPercent = 70 },
                new PumpSnapshot { Timestamp = Now.AddHours(-1).UtcDateTime, Reservoir = 118.5, BatteryPercent = 69 },
            ]);
        harness.Uploader.Setup(u => u.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UploaderSnapshot { Timestamp = Now.AddMinutes(-90).UtcDateTime, Battery = 55 }]);
        harness.Age.Setup(a => a.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo { SensorStart = new DeviceAgeInfo { Found = true, Age = 52, Days = 2, Hours = 4 } });
        var (runner, context, readLog) = harness.Arrange(Scope.DevicesRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 6 }, "panels": [
              { "id": "line", "dataset": { "name": "device_status" }, "primitive": "time_series" },
              { "id": "tiles", "dataset": { "name": "device_status" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None);

        harness.Pump.Verify(p => p.GetAsync(Now.AddHours(-6).UtcDateTime, Now.UtcDateTime, null, null, DeviceStatusDataset.RecordLimit, 0, false,
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        var line = result.Panels[0];
        line.Bindings.Y.Should().Equal("pumpBattery", "uploaderBattery");
        line.Rows.Select(r => (r["pumpBattery"], r["uploaderBattery"])).Should().Equal((70, null), (null, 55), (69, null));
        var tiles = result.Panels[1];
        tiles.Rows.Single().Should().Contain(new KeyValuePair<string, object?>("reservoir", 118.5))
            .And.Contain(new KeyValuePair<string, object?>("uploaderBattery", 55))
            .And.Contain(new KeyValuePair<string, object?>("sensorAgeHours", 52));
        tiles.Summary.Should().Be(
            "Device status from 2026-10-29 06:00 to 2026-10-29 12:00 UTC.\n"
            + "Reservoir: 118.5 U at 2026-10-29 11:00, lowest 118.5 U.\n"
            + "Pump battery: 69% at 2026-10-29 11:00, lowest 69%.\n"
            + "Uploader battery: 55% at 2026-10-29 10:30, lowest 55%.\n"
            + "Sensor age now: 2 days 4 hours.");
        readLog.Datasets.Should().Equal("device_status", "device_events");
    }

    [Fact]
    public async Task A_clamped_callers_tiles_say_why_the_sensor_age_is_missing()
    {
        var harness = new Harness();
        harness.Age.Setup(a => a.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(new SensorAgeInfo());
        var (runner, context, _) = harness.ArrangeClamped(Scope.DevicesRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 72 }, "panels": [ { "dataset": { "name": "device_status" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Summary.Should().Contain("Sensor age now: not shown. This token reads only the last 24 hours, and the latest sensor start can be older.");
    }

    [Fact]
    public async Task Without_devices_read_device_status_is_not_read()
    {
        var harness = new Harness();
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Status.Should().Be(ReportPanelStatus.NotPermitted);
        harness.Pump.VerifyNoOtherCalls();
        harness.Age.VerifyNoOtherCalls();
    }

    // ---- glucose_distribution ----

    [Fact]
    public async Task A_distribution_bar_carries_each_bands_colour_in_its_row()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Bindings.Should().BeEquivalentTo(new ReportBindings { X = "band", Y = ["percent"], Stacked = false, Orientation = ReportBarOrientation.Vertical });
        panel.Fields.Select(f => f.Name).Should().Equal("band", "percent", "color");
        panel.Fields.Single(f => f.Name == "band").ColorField.Should().Be("color");
        panel.Rows.Select(r => r["color"]).Should().Equal(
            ChartColor.GlucoseVeryLow, ChartColor.GlucoseLow, ChartColor.GlucoseInRange, ChartColor.GlucoseHigh, ChartColor.GlucoseVeryHigh);
        panel.Rows.Sum(r => (double)r["percent"]!).Should().BeApproximately(100, 0.5);
        readLog.Datasets.Should().Equal("glucose");
    }

    [Fact]
    public async Task A_distribution_bar_bound_to_the_period_is_one_row_with_the_bands_as_columns()
    {
        var (runner, context, _) = new Harness().Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "bar",
              "bindings": { "x": "period", "y": ["veryLow", "low", "inRange", "high", "veryHigh"], "stacked": true, "orientation": "horizontal" } } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Bindings.Orientation.Should().Be(ReportBarOrientation.Horizontal);
        panel.Bindings.Stacked.Should().BeTrue();
        panel.Fields.Select(f => f.Name).Should().Equal("period", "veryLow", "low", "inRange", "high", "veryHigh");
        panel.Fields.Skip(1).Select(f => f.Color).Should().Equal(
            ChartColor.GlucoseVeryLow, ChartColor.GlucoseLow, ChartColor.GlucoseInRange, ChartColor.GlucoseHigh, ChartColor.GlucoseVeryHigh);
        var row = panel.Rows.Should().ContainSingle().Subject;
        row["period"].Should().Be("2026-10-15 to 2026-10-29");
        new[] { "veryLow", "low", "inRange", "high", "veryHigh" }.Sum(k => (double)row[k]!).Should().BeApproximately(100, 0.5);
    }

    [Fact]
    public async Task Distribution_tiles_are_one_row_with_a_field_per_band()
    {
        var (runner, context, _) = new Harness().Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Fields.Select(f => f.Name).Should().Equal("veryLow", "low", "inRange", "high", "veryHigh");
        panel.Rows.Should().ContainSingle();
    }

    [Fact]
    public void The_distribution_summary_names_each_band_with_its_limits()
    {
        var tir = new TimeInRangeMetrics
        {
            Thresholds = new GlycemicThresholds(),
            Percentages = new TimeInRangePercentages { VeryLow = 0.4, Low = 1.6, Target = 71.8, High = 20.9, VeryHigh = 5.3 },
        };

        GlucoseDistributionDataset.Summarise(GlucoseDistributionDataset.Bands(tir), 4032, Now.AddDays(-14), Now, false).Should().Be(
            "Glucose distribution from 2026-10-15 12:00 to 2026-10-29 12:00 UTC.\n"
            + "Readings: 4032.\n"
            + "Time in each band: very low (below 54 mg/dL) 0.4%, low (54-70 mg/dL) 1.6%, in range (70-180 mg/dL) 71.8%, "
            + "high (180-250 mg/dL) 20.9%, very high (above 250 mg/dL) 5.3%.");
    }

    // ---- daily_summary ----

    [Fact]
    public async Task Daily_summary_with_glucose_only_reads_no_insulin_and_names_the_omitted_columns()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 72 }, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "table" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        harness.VerifyNoInsulinRead();
        panel.Fields.Select(f => f.Name).Should().NotContain(DailySummaryDataset.TreatmentFields);
        panel.Rows.Select(r => r["date"]).Should().Equal("2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29");
        panel.Summary.Should().StartWith("Daily summary over local days 2026-10-26 to 2026-10-29.")
            .And.EndWith("Fields omitted: totalInsulin, totalBolus, totalBasal, totalCarbs (scope not granted).");
        readLog.Datasets.Should().Equal("glucose");
    }

    [Fact]
    public async Task Daily_summary_with_treatments_reads_the_records_over_the_whole_local_days()
    {
        var harness = new Harness("Pacific/Auckland");
        harness.WithBoluses((Now.AddHours(-3), 5));
        harness.WithCarbs((Now.AddHours(-3), 40));
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 24 }, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "bar" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        // 2026-10-28 12:00Z to 2026-10-29 12:00Z is 2026-10-29 01:00 to 2026-10-30 01:00 in Auckland (UTC+13).
        var (start, _) = StatisticsService.PunchCardRange(new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        harness.Carbs.Verify(c => c.GetAsync(start, It.IsAny<DateTime?>(), null, null, int.MaxValue, 0, false, null, null, It.IsAny<CancellationToken>()));
        panel.Bindings.Should().BeEquivalentTo(new ReportBindings { X = "date", Y = ["timeBelow", "timeInRange", "timeAbove"], Stacked = true, Orientation = ReportBarOrientation.Vertical });
        var rows = panel.Rows.ToDictionary(r => (string)r["date"]!);
        rows.Keys.Should().Equal("2026-10-29", "2026-10-30");
        readLog.Datasets.Should().Equal("glucose", "treatments");
        panel.Summary.Should().Contain("Most carbs: 40 g on 2026-10-29.", "09:00Z is 22:00 on the 29th in Auckland");
    }

    [Fact]
    public async Task A_clamped_callers_first_day_is_flagged_partial()
    {
        var (runner, context, _) = new Harness().ArrangeClamped(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 72 }, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "table" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        // The clamp moves the start to 2026-10-28 12:00 UTC, half way through that day.
        panel.Rows.Select(r => (r["date"], r["partial"])).Should().Equal(("2026-10-28", (object?)true), ("2026-10-29", (object?)false));
        panel.Summary.Should().Contain("The day 2026-10-28 is partial: this token reads only the last 24 hours, so its row starts at 2026-10-28 12:00 UTC.");
    }

    [Fact]
    public async Task Daily_summary_without_a_time_zone_says_why_it_has_no_days()
    {
        var (runner, context, _) = new Harness(zone: null).Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "daily_summary" }, "primitive": "table" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Should().BeEmpty();
        panel.Summary.Should().Be("Daily summary: no data. The days cannot be placed without a time zone (NotConfigured). Set one in the profile.");
    }

    // ---- glucose_statistics insulin fields ----

    [Fact]
    public async Task Statistics_with_glucose_only_read_no_insulin_and_the_summary_names_the_omitted_fields()
    {
        var harness = new Harness();
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        harness.VerifyNoInsulinRead();
        panel.Fields.Select(f => f.Name).Should().NotContain(GlucoseStatisticsDataset.InsulinFields);
        panel.Summary.Should().EndWith("Fields omitted: tdd, basalPercent, carbsPerDay (scope not granted).");
        readLog.Datasets.Should().Equal("glucose");
    }

    [Fact]
    public async Task Statistics_with_treatments_add_insulin_and_carbs_per_day()
    {
        var harness = new Harness();
        harness.WithBoluses((Now.AddDays(-1), 10), (Now.AddHours(-30), 18));
        harness.WithCarbs((Now.AddDays(-1), 140));
        var (runner, context, readLog) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 48 }, "panels": [ { "dataset": { "name": "glucose_statistics" }, "primitive": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Single()["tdd"].Should().Be(14.0, "28 U of bolus over two days, no basal recorded");
        panel.Rows.Single()["carbsPerDay"].Should().Be(70.0);
        panel.Summary.Should().Contain("Insulin per day 14 U, 0% of it basal. Carbs per day 70 g.");
        readLog.Datasets.Should().Equal("glucose", "treatments");
    }

    // ---- glucose_series markers ----

    private static DashboardChartData ChartWithMarkers(long from)
    {
        var data = new DashboardChartData { Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250 } };
        for (var i = 0; i < 12; i++)
            data.GlucoseData.Add(new GlucosePointDto { Time = from + i * 300_000, Sgv = 110 + i });
        data.BolusMarkers.Add(new BolusMarkerDto { Time = from + 600_000, Insulin = 4.5, BolusType = Nocturne.Core.Models.BolusType.MealBolus });
        data.BolusMarkers.Add(new BolusMarkerDto { Time = from + 1_800_000, Insulin = 0.2, BolusType = Nocturne.Core.Models.BolusType.Smb });
        data.CarbMarkers.Add(new CarbMarkerDto { Time = from + 600_000, Carbs = 45 });
        return data;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_glucose_line_draws_bolus_and_carb_markers_only_for_a_token_that_reads_treatments(bool treatments)
    {
        var harness = new Harness();
        harness.Chart.Setup(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long _, int _, bool _, CancellationToken _) => ChartWithMarkers(from));
        var (runner, context, readLog) = treatments
            ? harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead)
            : harness.Arrange(Scope.GlucoseRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 1 }, "panels": [
              { "dataset": { "name": "glucose_series", "markers": ["bolus", "carbs"] }, "primitive": "time_series", "bindings": { "y": ["glucose"] } } ] }
            """), context, CancellationToken.None)).Panels[0];

        if (treatments)
        {
            panel.Markers.Select(m => (m.Kind, m.Amount, m.Automatic)).Should().Equal(
                (ReportMarkerKind.Bolus, 4.5, false), (ReportMarkerKind.Carbs, 45.0, false), (ReportMarkerKind.Bolus, 0.2, true));
            panel.Summary.Should().EndWith("Markers drawn: 2 boluses, 1 carb entry.");
            readLog.Datasets.Should().Contain("treatments");
        }
        else
        {
            panel.Markers.Should().BeEmpty();
            panel.Summary.Should().EndWith("Markers omitted: bolus, carbs (scope not granted: treatments.read).");
        }
    }
}
