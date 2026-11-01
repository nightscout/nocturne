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
using Nocturne.Core.Models.LoopAnalysis;
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
        public readonly Mock<ILoopAnalysisService> Loop = new();

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
                .AddSingleton(Age.Object).AddSingleton(Chart.Object).AddSingleton(Loop.Object)
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
              { "dataset": { "name": "treatments", "types": ["carbs", "note"], "minCarbs": 30 }, "kind": "table" } ] }
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
            + "BG checks: 0.\n"
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
            { "version": 1, "range": { "hours": 24 }, "panels": [ { "dataset": { "name": "treatments", "types": ["carbs"] }, "kind": "table" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Should().HaveCount(TreatmentsDataset.Budget);
        panel.Rows[^1]["time"].Should().Be(Now.ToUnixTimeMilliseconds(), "the latest are kept");
        panel.Resolution!.Downsampled.Should().BeTrue();
        panel.Summary.Should().Contain("Carb entries: 700").And.Contain($"The rows hold the latest {TreatmentsDataset.Budget} of 700 records");
    }

    [Fact]
    public async Task A_derived_source_reads_every_record_and_a_markers_layer_cut_to_the_budget_says_so()
    {
        var harness = new Harness();
        harness.WithBoluses([.. Enumerable.Range(0, 800).Select(i => (Now.AddMinutes(-15 * i - 1), 1.0))]);
        harness.WithCarbs((Now.AddHours(-1), 30));
        var (runner, context, _) = harness.Arrange(Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 216 }, "panels": [
              { "id": "c", "kind": "chart", "grid": 60, "tracks": [ { "layers": [
                { "id": "markers", "dataset": { "name": "treatments" }, "mark": "markers", "bindings": { "y": ["insulin"] } },
                { "id": "mean", "derived": { "op": "rolling_mean", "minutes": 60, "a": { "dataset": { "name": "treatments" }, "field": "insulin" } } } ] } ] } ] }
            """), context, CancellationToken.None);

        var panel = result.Panels.Single();
        var markers = panel.Tracks[0].Layers[0];
        var mean = panel.Tracks[0].Layers[1];
        mean.Limited.Should().BeFalse("a derived source reads every record of the range");
        mean.Rows.Select(r => r["value"]).OfType<double>().Count(v => v == 4.0).Should().BeGreaterThan(190,
            "four boluses of 1 U in each of the 200 hours they cover; a read cut to the latest 500 would give 0 before the last 125");
        markers.Limited.Should().BeTrue();
        markers.Sentence.Should().Contain($"Only the latest {TreatmentsDataset.Budget} of 800 records are shown");
        panel.Limited.Should().BeTrue();
        panel.Sentence.Should().Contain("Only the latest");
    }

    [Fact]
    public void A_chart_layer_reads_only_the_kinds_of_record_its_fields_draw()
    {
        var asked = TreatmentsDataset.Kinds(new TreatmentsParameters());

        TreatmentsDataset.KindsFor(asked, ["insulin", "carbs"]).Should().BeEquivalentTo([ReportTreatmentType.Bolus, ReportTreatmentType.Carbs]);
        TreatmentsDataset.KindsFor(asked, ["time"]).Should().BeEquivalentTo(asked);
        new TreatmentsDataset().ReadVariant(new ReportReadShape(["carbs", "insulin"])).Should().Be("carbs,insulin");
        new TreatmentsDataset().ReadVariant(ReportReadShape.Panel).Should().BeNull();
    }

    [Fact]
    public async Task The_read_cap_counts_the_reads_the_runner_makes()
    {
        var harness = new Harness();
        harness.WithBoluses((Now.AddHours(-3), 5), (Now.AddHours(-2), 1));
        harness.WithCarbs((Now.AddHours(-2), 40));
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);
        var validation = Validator.Validate(JsonDocument.Parse("""
            { "version": 1, "range": { "hours": 24 }, "panels": [ { "kind": "chart", "grid": 60, "tracks": [
              { "layers": [
                { "dataset": { "name": "treatments" }, "mark": "markers", "bindings": { "x": "time", "y": ["insulin", "carbs"] } },
                { "dataset": { "name": "treatments" }, "mark": "bars", "bindings": { "x": "time", "y": ["insulin"] } },
                { "dataset": { "name": "treatments" }, "mark": "bars", "bindings": { "x": "time", "y": ["insulin"] } } ] },
              { "layers": [
                { "derived": { "op": "rolling_mean", "minutes": 120, "a": { "dataset": { "name": "treatments" }, "field": "insulin" } } },
                { "derived": { "op": "rolling_mean", "minutes": 240, "a": { "dataset": { "name": "treatments" }, "field": "insulin" } } } ] } ] } ] }
            """).RootElement);
        validation.Errors.Should().BeEmpty();

        await runner.RunAsync(validation.Spec!, context, CancellationToken.None);

        validation.Reads.Should().Be(3, "boluses and carbs for the markers, boluses for both bars layers, and every bolus once for both sources");
        context.Cache.Reads.Should().Be(validation.Reads);
    }

    [Fact]
    public void A_treatments_layer_draws_its_amounts_as_the_dashboards_markers()
    {
        var layer = Spec(ReportSpecValidatorTests.Layers("""{ "dataset": { "name": "treatments" } }""")).Panels[0].Tracks![0].Layers[0];

        layer.Mark.Should().Be(ReportMark.Markers);
        layer.Bindings!.Y.Should().Equal("insulin", "injectionUnits", "carbs", "bgCheck");
        layer.HasAxis.Should().BeFalse();
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
              { "id": "line", "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "treatments" } } ] } ] },
              { "id": "rows", "dataset": { "name": "treatments" }, "kind": "table" } ] }
            """), context, CancellationToken.None);

        var line = result.Panels[0].Tracks[0].Layers[0];
        line.Markers.Select(m => (m.Kind, m.Amount, m.Automatic)).Should().Equal(
            (ReportMarkerKind.Bolus, 4.5, false), (ReportMarkerKind.Bolus, 0.3, true), (ReportMarkerKind.BasalInjection, 20, false));
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
            { "version": 1, "panels": [ { "dataset": { "name": "treatments" }, "kind": "table" } ] }
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
            { "version": 1, "range": { "hours": 6 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "basal_delivery" }, "mark": "line" } ] } ] } ] }
            """), context, CancellationToken.None)).Panels.Single().Tracks[0].Layers[0];

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
            { "version": 1, "range": { "hours": 6 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "basal_delivery" }, "mark": "line" } ] } ] } ] }
            """), context, CancellationToken.None)).Panels.Single().Tracks[0].Layers[0];

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
              { "dataset": { "name": "glucose_statistics" }, "kind": "stat_tiles" },
              { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_distribution_by_hour" } } ] } ] },
              { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_distribution" } } ] } ] } ] }
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
              { "id": "long", "dataset": { "name": "glucose_statistics" }, "kind": "stat_tiles", "range": { "hours": 2160 } },
              { "id": "short", "dataset": { "name": "glucose_statistics" }, "kind": "stat_tiles", "range": { "hours": 24 } } ] }
            """), context, CancellationToken.None);

        var full = result.Panels[0];
        full.AtRangeLimit.Should().BeTrue();
        full.RangeLimit.Should().Be("This panel covers at most 90 days.");
        result.Panels[1].AtRangeLimit.Should().BeFalse();
        result.Panels[1].RangeLimit.Should().BeNull();
    }

    /// <summary>
    /// Every figure of every dataset a chart draws names the fields it is about (<see cref="ReportFact.About"/>), so a
    /// layer prints only the figures about what it draws. The datasets this harness reads run whole; the rest are
    /// checked through the functions that build their figures.
    /// </summary>
    [Fact]
    public async Task Every_figure_of_a_dataset_a_chart_draws_names_the_fields_it_is_about()
    {
        var harness = new Harness();
        harness.WithBoluses((Now.AddHours(-3), 5));
        harness.WithCarbs((Now.AddHours(-2), 40));
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [
              { "id": "treatments", "dataset": { "name": "treatments" }, "kind": "table" },
              { "id": "days", "dataset": { "name": "daily_summary" }, "kind": "table" },
              { "id": "bands", "dataset": { "name": "glucose_distribution" }, "kind": "stat_tiles" },
              { "id": "hours", "dataset": { "name": "glucose_distribution_by_hour" }, "kind": "table" } ] }
            """), context, CancellationToken.None);

        var facts = result.Panels.SelectMany(p => p.Facts.Select(f => (p.Id, f))).ToList();
        facts.Should().NotBeEmpty();
        facts.Should().OnlyContain(x => x.f.About != null && x.f.About.Count > 0);

        var loop = new Harness();
        var aggregate = new LoopAnalysisAggregate { P10 = 80, P25 = 100, P50 = 130, P75 = 160, P90 = 200, N = 12 };
        loop.Loop.Setup(l => l.GetDailyProfileAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DailyProfileResponse
            {
                Primary = new DailyProfileRange
                {
                    Slots = [new DailyProfileSlot { MinuteOfDay = 0, Glucose = aggregate }, new DailyProfileSlot { MinuteOfDay = 5, Glucose = aggregate }],
                    Summary = new DailyProfileSummary { DaysWithData = 7, NetLoopCorrectionPerDay = 2.5 },
                },
            });
        loop.Loop.Setup(l => l.GetEventResponseAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<EventResponseDefinition>(), It.IsAny<TimeZoneInfo>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LoopAnalysisFilter _, EventResponseDefinition definition, TimeZoneInfo _, CancellationToken _) => new EventResponseReport
            {
                Primary = new EventResponseRange
                {
                    Cohorts = [.. definition.Cohorts.Select(c => new EventCohortResult
                    {
                        Cohort = c.Cohort,
                        Events = 3,
                        Response = EventResponseKind.Rise,
                        Slots = [new EventResponseSlot { Glucose = aggregate, GlucoseChange = aggregate }],
                    })],
                },
            });
        loop.Pump.Setup(p => p.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PumpSnapshot { Timestamp = Now.AddHours(-2).UtcDateTime, Reservoir = 120, BatteryPercent = 70 },
                new PumpSnapshot { Timestamp = Now.AddHours(-1).UtcDateTime, Reservoir = 118.5, BatteryPercent = 69 },
            ]);
        loop.Uploader.Setup(u => u.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UploaderSnapshot { Timestamp = Now.AddMinutes(-90).UtcDateTime, Battery = 55 }]);
        loop.Age.Setup(a => a.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo { SensorStart = new DeviceAgeInfo { Found = true, Age = 52, Days = 2, Hours = 4 } });
        var (loopRunner, loopContext, _) = loop.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead, Scope.FoodRead);

        var loopResult = await loopRunner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 168 }, "panels": [
              { "id": "profile", "dataset": { "name": "daily_profile" }, "kind": "table" },
              { "id": "events", "dataset": { "name": "event_response", "cohorts": [ { "anchor": "Meal" }, { "anchor": "CorrectionBolus" } ] }, "kind": "table" },
              { "id": "devices", "dataset": { "name": "device_status" }, "kind": "stat_tiles" } ] }
            """), loopContext, CancellationToken.None);

        loopResult.Panels.Should().OnlyContain(p => p.Status == ReportPanelStatus.Ok);
        foreach (var panel in loopResult.Panels)
            panel.Facts.Should().NotBeEmpty($"{panel.Id} has data").And.OnlyContain(f => f.About != null && f.About.Count > 0, panel.Id);

        var series = new List<BasalPoint> { new() { Timestamp = 0, Rate = 0.8, ScheduledRate = 1 }, new() { Timestamp = 1, Rate = 1.2, ScheduledRate = 1 } };
        BasalDeliveryDataset.FactsOf(series, therapy: true).Should().OnlyContain(f => f.About != null && f.About.Count > 0);
        SettingsEvidenceDataset.FactsOf([new Dictionary<string, object?> { ["setting"] = "isf", ["isfObserved"] = 40.0 }])
            .Should().OnlyContain(f => f.About != null && f.About.Count > 0);
    }

    [Fact]
    public async Task Every_panel_carries_at_most_six_facts_one_sentence_and_its_range_line()
    {
        var (runner, context, _) = new Harness().Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [
              { "id": "stats", "dataset": { "name": "glucose_statistics" }, "kind": "stat_tiles" },
              { "id": "hours", "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_distribution_by_hour" } } ] } ] },
              { "id": "bands", "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_distribution" } } ] } ] },
              { "id": "days", "dataset": { "name": "daily_summary" }, "kind": "table" },
              { "id": "compare", "dataset": { "name": "period_comparison", "preset": "last7-prior7" }, "kind": "comparison" } ] }
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
            { "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "kind": "stat_tiles" } ] }
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
              { "id": "line", "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "device_status" } } ] } ] },
              { "id": "tiles", "dataset": { "name": "device_status" }, "kind": "stat_tiles" } ] }
            """), context, CancellationToken.None);

        harness.Pump.Verify(p => p.GetAsync(Now.AddHours(-6).UtcDateTime, Now.UtcDateTime, null, null, DeviceStatusDataset.RecordLimit, 0, false,
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        var line = result.Panels[0].Tracks[0].Layers[0];
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
            { "version": 1, "range": { "hours": 72 }, "panels": [ { "dataset": { "name": "device_status" }, "kind": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Summary.Should().Contain("Sensor age now: not shown. This token reads only the last 24 hours, and the latest sensor start can be older.");
    }

    [Fact]
    public async Task Without_devices_read_device_status_is_not_read()
    {
        var harness = new Harness();
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var panel = (await runner.RunAsync(Spec("""
            { "version": 1, "panels": [ { "dataset": { "name": "device_status" }, "kind": "stat_tiles" } ] }
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
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_distribution" } } ] } ] } ] }
            """), context, CancellationToken.None)).Panels.Single().Tracks[0].Layers[0];

        panel.Bindings.Should().BeEquivalentTo(new ReportLayerBindings { X = "band", Y = ["percent"], Stacked = false, Orientation = ReportBarOrientation.Vertical });
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
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "glucose_distribution" }, "mark": "bars",
              "bindings": { "x": "period", "y": ["veryLow", "low", "inRange", "high", "veryHigh"], "stacked": true, "orientation": "horizontal" } } ] } ] } ] }
            """), context, CancellationToken.None)).Panels.Single().Tracks[0].Layers[0];

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
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_distribution" }, "kind": "stat_tiles" } ] }
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
            { "version": 1, "range": { "hours": 72 }, "panels": [ { "dataset": { "name": "daily_summary" }, "kind": "table" } ] }
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
            { "version": 1, "range": { "hours": 24 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [ { "dataset": { "name": "daily_summary" }, "mark": "bars", "bindings": { "x": "date" } } ] } ] } ] }
            """), context, CancellationToken.None)).Panels.Single().Tracks[0].Layers[0];

        // 2026-10-28 12:00Z to 2026-10-29 12:00Z is 2026-10-29 01:00 to 2026-10-30 01:00 in Auckland (UTC+13).
        var (start, _) = StatisticsService.PunchCardRange(new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        harness.Carbs.Verify(c => c.GetAsync(start, It.IsAny<DateTime?>(), null, null, int.MaxValue, 0, false, null, null, It.IsAny<CancellationToken>()));
        panel.Bindings.Should().BeEquivalentTo(new ReportLayerBindings { X = "date", Y = ["timeBelow", "timeInRange", "timeAbove"], Stacked = true, Orientation = ReportBarOrientation.Vertical });
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
            { "version": 1, "range": { "hours": 72 }, "panels": [ { "dataset": { "name": "daily_summary" }, "kind": "table" } ] }
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
            { "version": 1, "panels": [ { "dataset": { "name": "daily_summary" }, "kind": "table" } ] }
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
            { "version": 1, "panels": [ { "dataset": { "name": "glucose_statistics" }, "kind": "stat_tiles" } ] }
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
            { "version": 1, "range": { "hours": 48 }, "panels": [ { "dataset": { "name": "glucose_statistics" }, "kind": "stat_tiles" } ] }
            """), context, CancellationToken.None)).Panels.Single();

        panel.Rows.Single()["tdd"].Should().Be(14.0, "28 U of bolus over two days, no basal recorded");
        panel.Rows.Single()["carbsPerDay"].Should().Be(70.0);
        panel.Summary.Should().Contain("Insulin per day 14 U, 0% of it basal. Carbs per day 70 g.");
        readLog.Datasets.Should().Equal("glucose", "treatments");
    }
}
