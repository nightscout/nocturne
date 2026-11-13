using System.Text.Json;
using FluentAssertions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The names the engine gives a layer or panel the spec leaves untitled, from what sets its rows apart, and the data
/// the web reads in place of the summaries' prose: groups left out, day counts and the anchors.
/// </summary>
public class ReportNamingTests
{
    private static ReportSpec Spec(string json)
    {
        var validation = RecordDatasetTests.Validator.Validate(JsonDocument.Parse(json).RootElement);
        validation.Errors.Should().BeEmpty();
        return validation.Spec!;
    }

    [Theory]
    [InlineData("""{ "name": "daily_profile", "days": { "kind": "low" } }""", "Days with a low")]
    [InlineData("""{ "name": "daily_profile", "days": { "kind": "low", "others": true } }""", "All other days")]
    [InlineData("""{ "name": "daily_profile", "days": { "kind": "carbs", "minCarbs": 150 } }""", "Days with 150 g of carbs or more")]
    [InlineData("""{ "name": "daily_profile", "dayOfWeek": ["Fri", "Mon", "Wed"] }""", "Mon Wed Fri")]
    [InlineData("""{ "name": "daily_profile", "dayOfWeek": ["Sat", "Sun"], "days": { "kind": "exercise" } }""", "Days with exercise, Sat Sun")]
    [InlineData("""{ "name": "daily_summary", "days": { "kind": "illness", "others": true } }""", "All other days")]
    [InlineData("""{ "name": "daily_profile" }""", null)]
    [InlineData("""{ "name": "daily_profile", "dayOfWeek": ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] }""", null)]
    public void Days_and_weekdays_name_a_layer_without_reading_data(string dataset, string? name)
    {
        var parameters = JsonSerializer.Deserialize<ReportDatasetParameters>(dataset, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        ReportNames.Of(parameters).Should().Be(name);
    }

    [Fact]
    public async Task A_typical_day_of_some_days_is_named_by_them_and_gives_its_day_counts_as_data()
    {
        var harness = new RecordDatasetTests.Harness();
        harness.Loop.Setup(l => l.GetDailyProfileForDaysAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<DayCondition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LoopAnalysisFilter _, TimeZoneInfo _, DayCondition days, CancellationToken _) => new DailyProfileResponse
            {
                Primary = new DailyProfileRange
                {
                    Slots = [new DailyProfileSlot { MinuteOfDay = 0, Glucose = new LoopAnalysisAggregate { P10 = 80, P25 = 100, P50 = 130, P75 = 160, P90 = 200, N = 3 } }],
                    Summary = new DailyProfileSummary { DaysWithData = 4 },
                    DaySelection = days.Others
                        ? new DaySelection([.. Enumerable.Range(1, 23).Select(d => new DateOnly(2026, 10, d))], 4, 23, 1, 1)
                        : new DaySelection([.. Enumerable.Range(1, 4).Select(d => new DateOnly(2026, 10, d))], 4, 23, 1, 1),
                },
            });
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [
              { "id": "low", "dataset": { "name": "daily_profile", "days": { "kind": "low" } }, "mark": "band" },
              { "id": "rest", "dataset": { "name": "daily_profile", "days": { "kind": "low", "others": true } }, "mark": "line" },
              { "id": "gap", "derived": { "op": "difference",
                  "a": { "dataset": { "name": "daily_profile", "days": { "kind": "low" } }, "field": "glucoseP50" },
                  "b": { "dataset": { "name": "daily_profile", "days": { "kind": "low", "others": true } }, "field": "glucoseP50" } } } ] } ] } ] }
            """), context, CancellationToken.None);

        var panel = result.Panels[0];
        var layers = panel.Tracks![0].Layers;
        panel.Title.Should().Be("Days with a low", "an untitled panel takes the name its lead layer's data gave it");
        layers.Select(l => l.Title).Should().Equal("Days with a low", "All other days", "Glucose median, days with a low minus Glucose median, all other days");
        layers[0].Days.Should().Be(new ReportDayCounts(Kept: 4, Total: 29, Matching: 4, NotMatching: 23, Partial: 1, Undecided: 1));
        layers[1].Days!.Kept.Should().Be(23);
        JsonSerializer.Serialize(layers[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Contain("\"days\":{\"kept\":4,\"total\":29,\"matching\":4,\"notMatching\":23,\"partial\":1,\"undecided\":1}");
    }

    [Fact]
    public async Task A_given_title_is_kept_over_the_name_from_the_data()
    {
        var harness = new RecordDatasetTests.Harness();
        harness.Loop.Setup(l => l.GetDailyProfileForDaysAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<DayCondition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DailyProfileResponse { Primary = new DailyProfileRange { Slots = [new DailyProfileSlot()], Summary = new DailyProfileSummary() } });
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "chart", "title": "Mine", "tracks": [ { "layers": [
              { "title": "Mine too", "dataset": { "name": "daily_profile", "days": { "kind": "low" } }, "mark": "band" } ] } ] } ] }
            """), context, CancellationToken.None);

        result.Panels[0].Title.Should().Be("Mine");
        result.Panels[0].Tracks![0].Layers[0].Title.Should().Be("Mine too");
    }

    [Fact]
    public void Each_anchor_is_described_as_data_with_the_episode_rule_where_it_applies()
    {
        foreach (var name in new[] { "event_response", "event_outcomes", "event_outcome_spread", "event_occurrence" })
        {
            var anchors = ReportCatalogueTests.Catalogue().Datasets.Single(d => d.Name == name).Parameters.GetProperty("x-eventAnchors");
            anchors.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(Enum.GetNames<EventAnchorKind>());
            anchors.GetProperty("Meal").GetProperty("description").GetString().Should().Be("A carb entry; entries at the same instant are one meal.");
            anchors.GetProperty("Meal").TryGetProperty("rule", out var none).Should().BeTrue();
            none.ValueKind.Should().Be(JsonValueKind.Null);
            anchors.GetProperty("LowStart").GetProperty("rule").GetString().Should().StartWith("A low starts after at least 15 minutes of readings below 70 mg/dL");
            anchors.GetProperty("HighStart").GetProperty("rule").GetString().Should().Be(anchors.GetProperty("LowStart").GetProperty("rule").GetString());
        }
    }
}
