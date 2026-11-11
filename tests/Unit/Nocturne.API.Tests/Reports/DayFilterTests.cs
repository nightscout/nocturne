using System.Text.Json;
using FluentAssertions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>A typical day and the day-by-day rows over only the days that had something, or every other day.</summary>
public class DayFilterTests
{
    private static ReportSpec Spec(string json) =>
        RecordDatasetTests.Validator.Validate(JsonDocument.Parse(json).RootElement) is { IsValid: true } v
            ? v.Spec!
            : throw new InvalidOperationException(string.Join("; ", RecordDatasetTests.Validator.Validate(JsonDocument.Parse(json).RootElement).Errors));

    private static IReadOnlyList<ReportSpecError> Errors(string dataset, string days) =>
        RecordDatasetTests.Validator.Validate(JsonDocument.Parse($$"""
            { "version": 1, "panels": [ { "kind": "table", "dataset": { "name": "{{dataset}}", "days": {{days}} } } ] }
            """).RootElement).Errors;

    [Theory]
    [InlineData("""{ "kind": "exercise", "minCarbs": 100 }""", "Only a filter of carbs has carb limits. Remove them, or send kind carbs.")]
    [InlineData("""{ "kind": "carbs" }""", "Send minCarbs, maxCarbs, or both.")]
    [InlineData("""{ "kind": "carbs", "minCarbs": 2500 }""", "Send grams from 0 to 2000.")]
    [InlineData("""{ "kind": "carbs", "minCarbs": 200, "maxCarbs": 100 }""", "Send a lowest that is not above the highest.")]
    public void A_day_filter_out_of_its_limits_names_them(string days, string message)
    {
        foreach (var dataset in new[] { "daily_profile", "daily_summary" })
            Errors(dataset, days).Should().ContainSingle().Which.Message.Should().Be(message);
    }

    [Theory]
    [InlineData("""{ "kind": "exercise" }""", "days with exercise")]
    [InlineData("""{ "kind": "exercise", "others": true }""", "days without exercise")]
    [InlineData("""{ "kind": "lowOvernight" }""", "days with a low between 00:00 and 06:00")]
    [InlineData("""{ "kind": "carbs", "minCarbs": 150 }""", "days with 150 g of carbs or more")]
    [InlineData("""{ "kind": "carbs", "minCarbs": 150, "others": true }""", "days with less than 150 g of carbs")]
    [InlineData("""{ "kind": "carbs", "minCarbs": 100, "maxCarbs": 200, "others": true }""", "days with less than 100 or more than 200 g of carbs")]
    public void The_days_kept_are_named_in_words(string days, string words)
    {
        Errors("daily_profile", days).Should().BeEmpty();
        JsonSerializer.Deserialize<ReportDayFilter>(days, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Words().Should().Be(words);
    }

    [Fact]
    public void The_summary_line_counts_the_days_and_those_in_neither_group()
    {
        var filter = new ReportDayFilter { Kind = ReportDayKind.Exercise };

        filter.Summary(new DaySelection([new DateOnly(2026, 10, 2)], 1, 12, 1))
            .Should().Be("Only days with exercise: 1 of 13 days. 1 day starts before the history this token reads, so it is in neither group.");
    }

    [Fact]
    public async Task A_typical_day_of_only_the_days_with_exercise_reads_them_and_names_its_fields_by_them()
    {
        var harness = new RecordDatasetTests.Harness();
        DayCondition? asked = null;
        harness.Loop.Setup(l => l.GetDailyProfileForDaysAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<DayCondition>(), It.IsAny<CancellationToken>()))
            .Callback((LoopAnalysisFilter _, TimeZoneInfo _, DayCondition days, CancellationToken _) => asked = days)
            .ReturnsAsync(new DailyProfileResponse
            {
                Primary = new DailyProfileRange
                {
                    Slots = [new DailyProfileSlot { MinuteOfDay = 0, Glucose = new LoopAnalysisAggregate { P10 = 80, P25 = 100, P50 = 130, P75 = 160, P90 = 200, N = 12 } }],
                    Summary = new DailyProfileSummary { DaysWithData = 4 },
                    DaySelection = new DaySelection([new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 21)], 4, 10, 0),
                },
            });
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "chart", "tracks": [ { "layers": [
              { "id": "ex", "dataset": { "name": "daily_profile", "days": { "kind": "exercise" } }, "mark": "band" } ] } ] } ] }
            """), context, CancellationToken.None);

        asked!.Kind.Should().Be(DayConditionKind.Exercise);
        var layer = result.Panels[0].Tracks![0].Layers[0];
        layer.Summary.Should().StartWith("Only days with exercise: 4 of 14 days.");
        layer.Fields.Single(f => f.Name == "glucoseP50").Label.Should().Be("Glucose median, days with exercise");
    }

    [Fact]
    public async Task A_day_filter_on_exercise_without_treatments_shows_nothing_rather_than_every_day()
    {
        var harness = new RecordDatasetTests.Harness();
        var (runner, context, _) = harness.Arrange(Scope.GlucoseRead);

        var result = await runner.RunAsync(Spec("""
            { "version": 1, "range": { "hours": 336 }, "panels": [ { "kind": "table", "dataset": { "name": "daily_summary", "days": { "kind": "exercise" } } } ] }
            """), context, CancellationToken.None);

        result.Panels[0].Rows.Should().BeEmpty();
        result.Panels[0].Summary.Should().Be("Daily summary: not shown. Keeping only days with exercise needs the treatments.read scope, which this token does not hold.");
        harness.Loop.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_day_by_day_rows_of_days_with_a_low_and_of_every_other_day_add_up_to_every_day()
    {
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Rows(string days)
        {
            var harness = new RecordDatasetTests.Harness();
            var (runner, context, _) = harness.Arrange(Scope.GlucoseRead);
            var result = await runner.RunAsync(Spec($$"""
                { "version": 1, "range": { "hours": 168 }, "panels": [ { "kind": "table", "dataset": { "name": "daily_summary" {{days}} } } ] }
                """), context, CancellationToken.None);
            return result.Panels[0].Rows;
        }

        var every = await Rows("");
        var low = await Rows(""", "days": { "kind": "low" }""");
        var others = await Rows(""", "days": { "kind": "low", "others": true }""");

        (low.Count + others.Count).Should().Be(every.Count);
        low.Select(r => r["date"]).Should().NotIntersectWith(others.Select(r => r["date"]));
        low.Should().BeEmpty("the harness's glucose is under 70 mg/dL for 10 minutes at a time, and a low needs 15");
        others.Should().HaveCount(every.Count);
    }
}
