using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Mcp;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class ReportSpecRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ReportSpec Spec(string json) =>
        new ReportSpecValidator(ReportSpecValidatorTests.Registry).Validate(JsonDocument.Parse(json).RootElement) is { IsValid: true } v
            ? v.Spec!
            : throw new InvalidOperationException("The test spec is not valid.");

    /// <summary>One reading every five minutes over the window, with IOB, COB and basal series.</summary>
    private static DashboardChartData ChartData(long from, long to)
    {
        var data = new DashboardChartData
        {
            Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250, GlucoseYMax = 300 },
        };
        for (var t = from; t <= to; t += 5 * 60_000)
        {
            var i = (t - from) / (5 * 60_000);
            data.GlucoseData.Add(new GlucosePointDto { Time = t, Sgv = 100 + i % 50, Direction = "Flat" });
            data.IobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = 1.5 });
            data.CobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = 20 });
            data.BasalSeries.Add(new BasalPoint { Timestamp = t, Rate = 0.8 });
        }
        data.BolusMarkers.Add(new BolusMarkerDto { Time = from, Insulin = 2 });
        return data;
    }

    private static (ReportSpecRunner Runner, ReportRunContext Context, McpToolReadLog ReadLog) Arrange(
        IReportDatasetRegistry registry, params string[] scopes)
    {
        var chartData = new Mock<IChartDataService>();
        chartData
            .Setup(c => c.GetDashboardChartDataAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, int _, bool _, CancellationToken _) => ChartData(from, to));

        var services = new ServiceCollection()
            .AddSingleton(chartData.Object)
            .AddSingleton<IStatisticsService>(new StatisticsService(TimeProvider.System))
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
            .BuildServiceProvider();

        var readLog = new McpToolReadLog();
        var context = new ReportRunContext(false, scopes.ToHashSet(), services, readLog);
        return (new ReportSpecRunner(registry, new FixedTime(Now)), context, readLog);
    }

    [Fact]
    public async Task Fields_the_token_cannot_read_are_dropped_and_named()
    {
        var (runner, context, _) = Arrange(ReportSpecValidatorTests.Registry, Scope.GlucoseRead);
        var spec = Spec("""
            { "version": 1, "range": { "hours": 3 }, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "table", "bindings": { "columns": ["time", "glucose", "iob", "cob"] } } ] }
            """);

        var result = await runner.RunAsync(spec, context, CancellationToken.None);

        var panel = result.Panels.Single();
        panel.Status.Should().Be(ReportPanelStatus.Ok);
        panel.Fields.Select(f => f.Name).Should().Equal("time", "glucose");
        panel.Rows.Should().NotBeEmpty().And.OnlyContain(r => r.Keys.SequenceEqual(new[] { "time", "glucose" }));
        panel.Omitted.Select(o => o.Field).Should().Equal("iob", "cob", "basalRate");
        panel.Omitted.Should().OnlyContain(o => o.Scope == Scope.TreatmentsRead);
        panel.Summary.Should().Contain("Fields omitted: iob, cob, basalRate (scope not granted).");
    }

    [Fact]
    public async Task With_the_scope_the_fields_carry_the_series_at_each_reading()
    {
        var (runner, context, readLog) = Arrange(ReportSpecValidatorTests.Registry, Scope.GlucoseRead, Scope.TreatmentsRead);
        var spec = Spec("""
            { "version": 1, "range": { "hours": 1 }, "panels": [
              { "dataset": { "name": "glucose_series" }, "primitive": "table", "bindings": { "columns": ["glucose", "iob", "cob", "basalRate"] } } ] }
            """);

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single();

        panel.Omitted.Should().BeEmpty();
        panel.Rows[0].Should().Contain(new KeyValuePair<string, object?>("iob", 1.5))
            .And.Contain(new KeyValuePair<string, object?>("cob", 20.0))
            .And.Contain(new KeyValuePair<string, object?>("basalRate", 0.8));
        readLog.Datasets.Should().Equal("glucose", "treatments");
    }

    [Fact]
    public async Task A_long_range_is_downsampled_within_the_point_budget()
    {
        var (runner, context, _) = Arrange(ReportSpecValidatorTests.Registry, Scope.GlucoseRead);
        var spec = Spec("""
            { "version": 1, "range": { "hours": 720 }, "panels": [
              { "dataset": { "name": "glucose_series", "intervalMinutes": 5 }, "primitive": "time_series", "bindings": { "y": "glucose" } } ] }
            """);

        var panel = (await runner.RunAsync(spec, context, CancellationToken.None)).Panels.Single();

        panel.Rows.Count.Should().BeLessThanOrEqualTo(GlucoseSeriesDataset.Budget).And.BeGreaterThan(GlucoseSeriesDataset.Budget / 2);
        panel.Resolution!.IntervalMinutes.Should().Be(22, "30 days of minutes over 2000 points, rounded up");
        panel.Resolution.RequestedIntervalMinutes.Should().Be(5);
        panel.Resolution.SourceRows.Should().Be(30 * 288 + 1);
        panel.Resolution.Downsampled.Should().BeTrue();
        panel.Summary.Should().Contain($"Readings: {30 * 288 + 1}.", "the summary covers every reading, not the sampled rows");
    }

    [Fact]
    public void Downsampling_keeps_real_readings_one_per_slot()
    {
        var readings = Enumerable.Range(0, 12).Select(i => new GlucosePointDto { Time = i * 60_000L, Sgv = 100 + i }).ToList();

        var kept = GlucoseSeriesDataset.Downsample(readings, 0, 5);

        kept.Select(r => r.Sgv).Should().Equal(100, 105, 110);
    }

    [Fact]
    public async Task The_glucose_chart_carries_the_redacted_dashboard_payload()
    {
        var (runner, context, readLog) = Arrange(ReportSpecValidatorTests.Registry, Scope.GlucoseRead);

        var result = await runner.RunAsync(Nocturne.API.Mcp.Tools.GlucoseTools.Preset(3), context, CancellationToken.None);

        var panel = result.Panels.Single();
        panel.Primitive.Should().Be(ReportPrimitive.GlucoseChart);
        panel.Rows.Should().BeEmpty();
        panel.Chart!.GlucoseData.Should().HaveCount(37);
        panel.Chart.BolusMarkers.Should().BeEmpty("treatments.read was not granted");
        panel.OmittedScopes.Should().Contain(Scope.TreatmentsRead);
        result.Summary.Should().StartWith("Glucose from 2026-10-29 09:00 to 2026-10-29 12:00 UTC.")
            .And.Contain("Tracks omitted: insulin, carbs and basal");
        readLog.Datasets.Should().Equal("glucose");
    }

    [Fact]
    public async Task Summaries_are_joined_under_the_panel_ids()
    {
        var (runner, context, _) = Arrange(ReportSpecValidatorTests.Registry, Scope.GlucoseRead);
        var spec = Spec("""
            { "version": 1, "title": "Two panels", "range": { "hours": 2 }, "panels": [
              { "id": "a", "title": "Glucose", "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": "glucose", "band": ["targetLow", "targetHigh"] } },
              { "id": "b", "dataset": { "name": "glucose_series" }, "primitive": "table" } ] }
            """);

        var result = await runner.RunAsync(spec, context, CancellationToken.None);

        result.Summary.Should().StartWith("Two panels\n\n[a] Glucose\nGlucose from 2026-10-29 10:00")
            .And.Contain("\n\n[b] glucose_series\nGlucose from");
        result.Panels[0].Fields.Select(f => f.Name).Should().Equal("time", "glucose", "targetLow", "targetHigh");
        result.Panels[0].Fields.Single(f => f.Name == "glucose").Unit.Should().Be("mg/dL");
        result.Panels[0].Thresholds.Should().Be(new ReportThresholds(54, 70, 180, 250));
    }

    [Fact]
    public async Task A_dataset_whose_scope_the_token_lacks_is_not_read()
    {
        var slow = new FakeDataset("slow_series", Scope.HeartRateRead, TimeSpan.Zero);
        var (runner, context, readLog) = Arrange(new ReportDatasetRegistry([slow]), Scope.GlucoseRead);

        var result = await runner.RunAsync(FakeSpec("slow_series"), context, CancellationToken.None);

        result.Panels.Single().Status.Should().Be(ReportPanelStatus.NotPermitted);
        result.Panels.Single().Summary.Should().Contain("'heartrate.read'");
        slow.Runs.Should().Be(0);
        readLog.Datasets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_report_that_runs_out_of_time_returns_its_finished_panels_and_names_the_rest()
    {
        var fast = new FakeDataset("fast", Scope.GlucoseRead, TimeSpan.Zero);
        var stuck = new FakeDataset("stuck", Scope.GlucoseRead, Timeout.InfiniteTimeSpan);
        var (runner, context, _) = Arrange(new ReportDatasetRegistry([fast, stuck]), Scope.GlucoseRead);
        runner = new ReportSpecRunner(new ReportDatasetRegistry([fast, stuck]), TimeProvider.System) { Timeout = TimeSpan.FromMilliseconds(200) };

        var result = await runner.RunAsync(FakeSpec("fast", "stuck", "fast"), context, CancellationToken.None);

        result.Panels.Select(p => p.Status).Should().Equal(ReportPanelStatus.Ok, ReportPanelStatus.TimedOut, ReportPanelStatus.TimedOut);
        result.Panels[0].Rows.Should().HaveCount(1);
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("Panels p2, p3 did not finish");
        fast.Runs.Should().Be(1, "nothing runs after the deadline");
    }

    [Fact]
    public async Task A_caller_that_cancels_is_not_reported_as_a_timeout()
    {
        var stuck = new FakeDataset("stuck", Scope.GlucoseRead, Timeout.InfiniteTimeSpan);
        var (runner, context, _) = Arrange(new ReportDatasetRegistry([stuck]), Scope.GlucoseRead);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var run = () => runner.RunAsync(FakeSpec("stuck"), context, cancelled.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ReportSpec FakeSpec(params string[] datasets) => new()
    {
        Panels = [.. datasets.Select((name, i) => new ReportPanelSpec
        {
            Id = $"p{i + 1}",
            Dataset = new FakeParameters(name),
            Primitive = ReportPrimitive.Table,
            Bindings = new ReportBindings { Columns = ["value"] },
            Range = new ReportRange { Hours = 1 },
        })],
    };

    private sealed record FakeParameters(string Name) : ReportDatasetParameters
    {
        public override string DatasetName => Name;
    }

    private sealed class FakeDataset(string name, string scope, TimeSpan delay) : IReportDataset
    {
        public int Runs { get; private set; }

        public string Name => name;
        public string Description => name;
        public Type ParametersType => typeof(FakeParameters);
        public string RequiredScope => scope;
        public IReadOnlyList<ReportField> Fields { get; } = [new("value", "Value", ReportFieldKind.Number, null, "A value.")];
        public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.Table];
        public TimeSpan DefaultRange => TimeSpan.FromHours(1);
        public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(1);
        public int PointBudget => 1;
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } = [];
        public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

        public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
        {
            Runs++;
            await Task.Delay(delay, cancellationToken);
            return new ReportDatasetOutput { Rows = [new Dictionary<string, object?> { ["value"] = 1 }], Summary = name };
        }
    }
}
