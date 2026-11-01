using FluentAssertions;
using Nocturne.API.Reports;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>Folding a layer's rows onto a chart's grid by each field's aggregate.</summary>
public class ReportGridTests
{
    private const long Hour = 3_600_000;

    private static IReadOnlyDictionary<string, object?> Row(long time, params (string, object?)[] values)
    {
        var row = new Dictionary<string, object?> { ["time"] = time };
        foreach (var (name, value) in values)
            row[name] = value;
        return row;
    }

    private static ReportField Field(string name, ReportAggregate aggregate, bool step = false) =>
        new(name, name, ReportFieldKind.Number, "U", name, Step: step, Aggregate: aggregate);

    [Fact]
    public void Each_field_folds_by_its_own_aggregate_and_empty_slots_are_left_out()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            Row(0, ("bolus", 1.0), ("glucose", 100.0), ("rate", 0.5), ("peak", 3.0)),
            Row(20 * 60_000, ("bolus", 2.0), ("glucose", 120.0), ("rate", 0.7), ("peak", 7.0)),
            Row(2 * Hour, ("bolus", null), ("glucose", 90.0), ("rate", null), ("peak", 1.0)),
        ];
        ReportField[] fields =
        [
            Field("bolus", ReportAggregate.Sum), Field("glucose", ReportAggregate.Mean), Field("rate", ReportAggregate.Last), Field("peak", ReportAggregate.Max),
        ];

        var folded = ReportGrid.FoldRows(rows, "time", fields, null, [], ReportXKind.Time, 60, 0, 3 * Hour);

        folded.Select(r => r["time"]).Should().Equal(0L, 2 * Hour);
        folded[0].Should().Contain(new KeyValuePair<string, object?>("bolus", 3.0)).And.Contain(new KeyValuePair<string, object?>("glucose", 110.0))
            .And.Contain(new KeyValuePair<string, object?>("rate", 0.7)).And.Contain(new KeyValuePair<string, object?>("peak", 7.0));
        folded[1]["bolus"].Should().BeNull("no bolus in the slot is no value, never zero");
    }

    [Fact]
    public void A_step_field_holds_its_value_into_the_slots_after_it_with_none()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [Row(0, ("rate", 0.8)), Row(3 * Hour + 1, ("rate", 1.2))];
        var rate = Field("rate", ReportAggregate.Last, step: true);
        var bolus = Field("rate", ReportAggregate.Sum);

        var step = ReportGrid.Values(rows, "time", rate, ReportXKind.Time, 60, 0, 5 * Hour);
        var (keys, series) = ReportGrid.Align(ReportXKind.Time, 60, 0, 5 * Hour,
            [new GridSeries(step, Carry: true), new GridSeries(ReportGrid.Values(rows, "time", bolus, ReportXKind.Time, 60, 0, 5 * Hour))]);

        keys.Should().HaveCount(5);
        series[0].Should().Equal(0.8, 0.8, 0.8, 1.2, 1.2);
        series[1].Should().Equal(0.8, null, null, 1.2, null);
    }

    [Fact]
    public void A_summed_field_is_zero_in_every_empty_slot_its_dataset_read_and_empty_outside_it()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [Row(Hour + 1, ("carbs", 30.0))];
        var carbs = Field("carbs", ReportAggregate.Sum);

        var values = ReportGrid.Values(rows, "time", carbs, ReportXKind.Time, 60, 0, 5 * Hour);
        var (_, series) = ReportGrid.Align(ReportXKind.Time, 60, 0, 5 * Hour, [new GridSeries(values, Zeros: (0, 4 * Hour))]);

        series[0].Should().Equal(0.0, 30.0, 0.0, 0.0, null);
    }

    [Fact]
    public void A_scatter_of_glucose_against_carbs_pairs_every_slot_with_a_reading()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> glucose =
            [.. Enumerable.Range(0, 12).Select(i => Row(i * 15 * 60_000L, ("glucose", 100.0 + i)))];
        IReadOnlyList<IReadOnlyDictionary<string, object?>> carbs = [Row(30 * 60_000, ("carbs", 40.0))];
        var a = ReportGrid.Values(carbs, "time", Field("carbs", ReportAggregate.Sum), ReportXKind.Time, 15, 0, 3 * Hour);
        var b = ReportGrid.Values(glucose, "time", Field("glucose", ReportAggregate.Mean), ReportXKind.Time, 15, 0, 3 * Hour);

        var (keys, series) = ReportGrid.Align(ReportXKind.Time, 15, 0, 3 * Hour, [new GridSeries(a, Zeros: (0, 3 * Hour)), new GridSeries(b)]);

        var pairs = keys.Select((_, i) => (series[0][i], series[1][i])).Where(p => p.Item1 is not null && p.Item2 is not null).ToList();
        pairs.Should().HaveCount(12, "a slot with no carb entry is 0 g of carbs, so every reading is paired");
        pairs.Count(p => p.Item1 == 0).Should().Be(11);
    }

    [Fact]
    public void Time_slots_lie_on_whole_steps_from_the_epoch_wherever_the_range_starts()
    {
        const long Minute = 60_000;
        var (first, _) = ReportGrid.Align(ReportXKind.Time, 15, 7 * Minute, Hour, [new GridSeries([])]);
        var (second, _) = ReportGrid.Align(ReportXKind.Time, 15, 11 * Minute, Hour + 4 * Minute, [new GridSeries([])]);

        first.Should().Equal(0, 15 * Minute, 30 * Minute, 45 * Minute);
        second.Should().Equal(0, 15 * Minute, 30 * Minute, 45 * Minute, Hour);
        ReportGrid.Slot(ReportXKind.Time, 7 * Minute + 0.5, 15, 7 * Minute).Should().Be(0);
        ReportGrid.Slot(ReportXKind.TimeOfDay, 1439, 60, 0).Should().Be(1380, "a time-of-day slot counts from local midnight");
    }

    [Fact]
    public void Rows_outside_the_range_are_not_folded_in()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [Row(-1, ("g", 500.0)), Row(10, ("g", 100.0)), Row(Hour, ("g", 700.0))];

        var folded = ReportGrid.FoldRows(rows, "time", [Field("g", ReportAggregate.Mean)], null, [], ReportXKind.Time, 60, 0, Hour);

        folded.Should().ContainSingle().Which["g"].Should().Be(100.0);
    }

    [Fact]
    public void Offsets_fold_on_minutes_from_zero_and_each_series_folds_apart()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["minute"] = -10, ["series"] = "A", ["g"] = 1.0, ["cohort"] = 1 },
            new Dictionary<string, object?> { ["minute"] = -5, ["series"] = "A", ["g"] = 3.0, ["cohort"] = 1 },
            new Dictionary<string, object?> { ["minute"] = -5, ["series"] = "B", ["g"] = 9.0, ["cohort"] = 2 },
        ];

        var folded = ReportGrid.FoldRows(rows, "minute", [Field("g", ReportAggregate.Mean)], "series", ["cohort"], ReportXKind.Offset, 15, 0, 0);

        folded.Select(r => (r["series"], r["minute"], r["g"], r["cohort"])).Should().Equal(("A", -15L, 2.0, 1), ("B", -15L, 9.0, 2));
    }

    [Fact]
    public void A_long_time_range_coarsens_the_grid_to_its_slot_budget()
    {
        var from = DateTimeOffset.UnixEpoch;

        ReportGrid.Step(ReportXKind.Time, 5, from, from.AddDays(30)).Should().Be(22);
        ReportGrid.Step(ReportXKind.Time, 60, from, from.AddDays(30)).Should().Be(60);
        ReportGrid.Step(ReportXKind.TimeOfDay, 5, from, from.AddDays(90)).Should().Be(5);
        ReportGrid.Step(ReportXKind.Day, 5, from, from.AddDays(90)).Should().Be(1440);
    }
}
