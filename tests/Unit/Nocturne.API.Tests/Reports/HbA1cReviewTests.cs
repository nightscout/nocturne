using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Services;
using Xunit;

namespace Nocturne.API.Tests.Reports;

public class HbA1cReviewTests
{
    [Theory]
    [InlineData(false, "%")]
    [InlineData(true, "mmol/mol")]
    public void Validation_compares_display_units_and_names_them_in_errors(bool mmolMol, string unit)
    {
        var json = ReportSpecValidatorTests.Layers("""
            { "dataset": { "name": "lab_hba1c" }, "mark": "points" },
            { "dataset": { "name": "daily_summary" }, "mark": "points", "bindings": { "x": "day", "y": ["timeInRange"] } },
            { "dataset": { "name": "ehba1c" }, "mark": "points", "bindings": { "x": "day", "y": ["weightedMean"] } }
            """);
        var validation = new ReportSpecValidator(ReportSpecValidatorTests.Registry, TimeProvider.System)
            .Validate(JsonDocument.Parse(json).RootElement, false, mmolMol);
        if (mmolMol) validation.Errors.Should().Contain(e => e.Message.Contains(unit));
        else validation.Errors.Should().BeEmpty();
        validation.Errors.Should().NotContain(e => e.Message.Contains(" in a1c"));
    }

    [Theory]
    [InlineData(false, "%")]
    [InlineData(true, "mmol/mol")]
    public void The_picker_resolves_hba1c_units_and_defaults_single_day_estimates_to_points(bool mmolMol, string unit)
    {
        var workspace = ReportWorkspace.Build(ReportSpecValidatorTests.Registry, new HashSet<string> { Scope.GlucoseRead }, mmolMol);
        workspace.Signals.Where(s => s.Id is "eHbA1c" or "labHbA1c").Should().OnlyContain(s => s.Unit == unit);
        workspace.Cells.Where(c => c.Signal == "eHbA1c").Should().OnlyContain(c => c.Unit == unit && c.Marks[0].Mark == "points");
    }

    [Fact]
    public async Task Restricted_history_does_not_read_or_emit_estimates()
    {
        var overview = new Mock<IDataOverviewService>(MockBehavior.Strict);
        var request = Request(overview.Object) with { HistoryClamped = true };
        var result = await new EHbA1cDataset().RunAsync(request, CancellationToken.None);
        result.Rows.Should().BeEmpty();
        overview.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Empty_estimates_keep_source_read_counts_and_explain_the_reading_threshold()
    {
        var overview = new Mock<IDataOverviewService>();
        overview.Setup(o => o.GetEHbA1cTimelineAsync(2026, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EHbA1cTimelineResponse { SensorReadingCount = 12, MeterReadingCount = 17 });
        var request = Request(overview.Object);
        var result = await new EHbA1cDataset().RunAsync(request, CancellationToken.None);
        result.Rows.Should().BeEmpty();
        request.ReadLog.RecordCount.Should().Be(29);
        request.ReadLog.Datasets.Should().Equal("glucose", "meter_glucose");
        result.Sentence.Should().Contain("30 glucose readings");
    }

    [Fact]
    public async Task An_unavailable_zone_is_not_reported_as_missing_readings()
    {
        var overview = new Mock<IDataOverviewService>();
        overview.Setup(o => o.GetEHbA1cTimelineAsync(2026, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EHbA1cTimelineResponse { TimeZoneUnavailableReason = Enum.GetValues<TimeZoneUnavailableReason>()[0] });
        var result = await new EHbA1cDataset().RunAsync(Request(overview.Object), CancellationToken.None);
        result.Sentence.Should().Contain("time zone");
    }

    private static ReportDatasetRequest Request(IDataOverviewService overview) => new(new EHbA1cParameters(), ReportPanelKind.Chart,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        false, new HashSet<string> { Scope.GlucoseRead }, new ServiceCollection().AddSingleton(overview).BuildServiceProvider(), new ReportReadLog(), Zone: TimeZoneInfo.Utc);
}
