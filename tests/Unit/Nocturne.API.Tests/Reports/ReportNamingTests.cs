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
        panel.Title.Should().BeNull("the lead's name would head both groups as if it were one; the host's caption names the comparison");
        panel.Context.Should().BeNull("the lead's range line counts only its own days");
        layers.Select(l => l.Title).Should().Equal("Glucose", "Glucose", "Glucose median, days with a low minus Glucose median, all other days");
        layers.Select(l => l.Group).Should().Equal("Days with a low", "All other days", null);
        layers[0].Days.Should().Be(new ReportDayCounts(Kept: 4, Total: 29, Matching: 4, NotMatching: 23, Partial: 1, Undecided: 1));
        layers[1].Days!.Kept.Should().Be(23);
        JsonSerializer.Serialize(layers[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Contain("\"days\":{\"kept\":4,\"total\":29,\"matching\":4,\"notMatching\":23,\"partial\":1,\"undecided\":1}");
    }

    [Theory]
    [InlineData("Glucose median", "Glucose")]
    [InlineData("Glucose P25", "Glucose")]
    [InlineData("IOB median", "Insulin on board")]
    [InlineData("COB", "Carbs on board")]
    [InlineData("Net loop correction median", "Net loop correction")]
    [InlineData("Basal rate", "Basal rate")]
    [InlineData("P50", "P50")]
    public void A_field_is_named_by_its_signal_without_the_statistic(string label, string signal) =>
        ReportNames.Signal(label).Should().Be(signal);

    [Theory]
    [InlineData(EventAnchorKind.LowStart, 0, 360, true, "Lows that start from 12:00 AM to 6:00 AM", "No lows started from 12:00 AM to 6:00 AM")]
    [InlineData(EventAnchorKind.LowStart, 0, 360, false, "Lows that start from 00:00 to 06:00", "No lows started from 00:00 to 06:00")]
    [InlineData(EventAnchorKind.Meal, 390, 600, true, "Meals from 6:30 AM to 10:00 AM", "No meals from 6:30 AM to 10:00 AM")]
    [InlineData(EventAnchorKind.HighStart, null, null, true, "Highs", "No highs")]
    public void An_event_group_is_written_in_words_on_the_tenants_clock(
        EventAnchorKind anchor, int? from, int? to, bool hour12, string heading, string none)
    {
        var words = Nocturne.API.Reports.Datasets.EventResponseDataset.Words(
            new EventCohortDefinition { Anchor = anchor, FromMinute = from, ToMinute = to }, new Dictionary<Guid, string>(), true, false, hour12);

        words.Should().Be(new Nocturne.API.Reports.Datasets.EventResponseDataset.CohortWords(heading, none));
    }

    [Fact]
    public void An_event_groups_filters_follow_its_name_and_its_weekdays_come_last()
    {
        var words = Nocturne.API.Reports.Datasets.EventResponseDataset.Words(
            new EventCohortDefinition { Anchor = EventAnchorKind.Meal, MinGrams = 40, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday] },
            new Dictionary<Guid, string>(), true, false, true);

        words.Heading.Should().Be("Meals with at least 40 g on Mon, Tue");
        Nocturne.API.Reports.Datasets.EventResponseDataset.Words(
                new EventCohortDefinition { Anchor = EventAnchorKind.Meal, Others = true }, new Dictionary<Guid, string>(), true, false, true)
            .Heading.Should().Be("All other meals");
    }

    [Fact]
    public void A_group_with_no_events_says_what_was_looked_for_and_where()
    {
        var now = new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero);
        ReportDatasetRequest Request(DateTimeOffset to) => new(null!, ReportPanelKind.Chart, to.AddDays(-90), to, false,
            new HashSet<string>(), null!, new ReportReadLog(), Now: now);
        var words = new Nocturne.API.Reports.Datasets.EventResponseDataset.CohortWords("Lows that start from 12:00 AM to 6:00 AM", "No lows started from 12:00 AM to 6:00 AM");

        Nocturne.API.Reports.Datasets.EventResponseDataset.NoneSentence(words, Nocturne.API.Reports.Datasets.EventResponseDataset.RangeWords(Request(now)))
            .Should().Be("No lows started from 12:00 AM to 6:00 AM in the last 90 days.");
        Nocturne.API.Reports.Datasets.EventResponseDataset.RangeWords(Request(now.AddDays(-100)))
            .Should().Be("from 2026-05-10 to 2026-08-08");
    }

    [Fact]
    public void A_lane_is_named_by_each_signal_it_draws_with_its_unit()
    {
        ReportLayerResult Layer(string title, string? unit, ReportMark mark = ReportMark.Line) => new()
        {
            Id = title, Title = title, Mark = mark, Bindings = new ReportLayerBindings(), XKind = ReportXKind.Offset, Unit = unit,
            Side = ReportAxisSide.Left, Status = ReportPanelStatus.Ok, Window = new ReportWindow(0, 1),
        };

        ReportSpecRunner.LaneTitle(new ReportTrackResult("t", null, 1, ReportXKind.Offset, [Layer("Insulin on board", "U")]))
            .Should().Be("Insulin on board (U)");
        ReportSpecRunner.LaneTitle(new ReportTrackResult("t", null, 1, ReportXKind.Time, [Layer("Modes", null, ReportMark.Spans)]))
            .Should().BeNull("a lane of spans names each of its rows");
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
