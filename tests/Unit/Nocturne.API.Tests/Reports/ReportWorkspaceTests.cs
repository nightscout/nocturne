using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The workspace vocabulary in the catalogue: every way it draws a signal, and every derived layer a cell declares,
/// is a spec the validator takes, so the vocabulary cannot drift from the datasets.
/// </summary>
public class ReportWorkspaceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CustomReportWorkspace Workspace(params string[] scopes) => ReportCatalogueTests.Catalogue(false, scopes).Workspace;

    /// <summary>The cell's dataset object: its name and the parameters it always sends.</summary>
    private static JsonObject Dataset(CustomReportWorkspaceCell cell)
    {
        var dataset = cell.Parameters is { } parameters ? JsonNode.Parse(parameters.GetRawText())!.AsObject() : [];
        dataset.Insert(0, "name", cell.Dataset);
        return dataset;
    }

    private static JsonObject Source(CustomReportWorkspaceCell cell) => new() { ["dataset"] = Dataset(cell), ["field"] = cell.Field };

    private static string Chart(JsonObject layer, int hours = 336, int? grid = null) =>
        new JsonObject
        {
            ["version"] = 1,
            ["range"] = new JsonObject { ["hours"] = hours },
            ["panels"] = new JsonArray(new JsonObject
            {
                ["kind"] = "chart",
                ["grid"] = grid,
                ["tracks"] = new JsonArray(new JsonObject { ["layers"] = new JsonArray(layer) }),
            }),
        }.ToJsonString();

    /// <summary>The grid a smoothed timeline of <paramref name="hours"/> is drawn on, by the rule the workspace states.</summary>
    private static int TimelineGrid(CustomReportWorkspace workspace, int hours)
    {
        var least = (int)Math.Ceiling(hours * 60.0 / workspace.MaxTimeSlots);
        return workspace.TimelineGrids.FirstOrDefault(g => g >= least, workspace.TimelineGrids[^1]);
    }

    /// <summary>
    /// Every spec the vocabulary can make at each range it offers, by what it is. A range longer than a cell's
    /// dataset covers (its catalogue <c>maxRangeHours</c>) is the web's to split or refuse, so it is not drawn here.
    /// </summary>
    private static List<(string What, string Spec)> Specs(CustomReportWorkspace workspace, CustomReportCatalogue catalogue)
    {
        var covers = catalogue.Datasets.ToDictionary(d => d.Name, d => d.MaxRangeHours);
        var specs = new List<(string, string)>();
        foreach (var range in workspace.RangePresets)
        {
            var hours = range.Hours;
            bool Covered(CustomReportWorkspaceCell cell) => covers[cell.Dataset] >= hours;
            foreach (var cell in workspace.Cells.Where(c => c.Lens != "against" && Covered(c)))
            {
                var at = $"{range.Id} {cell.Lens}/{cell.Signal}";
                int? grid = cell.Lens == "timeline" ? TimelineGrid(workspace, hours) : null;
                int Window(int minutes) => grid is { } g ? (int)Math.Ceiling(minutes / (double)g) * g : minutes;
                foreach (var mark in cell.Marks.Concat(cell.FromStartMarks ?? []))
                    specs.Add(($"{at} as {mark.Word}", Chart(new JsonObject
                    {
                        ["dataset"] = Dataset(cell),
                        ["mark"] = mark.Mark,
                        ["bindings"] = Bindings(mark),
                    }, hours)));
                foreach (var minutes in cell.Smooth)
                    foreach (var op in new[] { "rolling_mean", "band_sd" })
                        specs.Add(($"{at} {op} {minutes}", Chart(new JsonObject
                        {
                            ["derived"] = new JsonObject { ["op"] = op, ["minutes"] = Window(minutes), ["a"] = Source(cell) },
                        }, hours, grid)));
                if (cell.Trend)
                    specs.Add(($"{at} trend", Chart(new JsonObject { ["derived"] = new JsonObject { ["op"] = "trend", ["a"] = Source(cell) } }, hours, grid)));
                if (cell.Combine)
                    foreach (var op in workspace.CombineOps)
                        specs.Add(($"{at} {op}", Chart(new JsonObject
                        {
                            ["derived"] = new JsonObject { ["op"] = op, ["a"] = Source(cell), ["b"] = Source(cell) },
                        }, hours, grid)));
            }
            var against = workspace.Cells.Where(c => c.Lens == "against" && Covered(c)).ToList();
            foreach (var a in against)
                foreach (var b in against.Where(b => b != a))
                    foreach (var op in new[] { "scatter", "lag_correlation" })
                        specs.Add(($"{range.Id} against {op} {a.Signal} x {b.Signal}", Chart(new JsonObject
                        {
                            ["derived"] = new JsonObject { ["op"] = op, ["a"] = Source(a), ["b"] = Source(b) },
                        }, hours, workspace.AgainstGridMinutes)));
        }
        return specs;
    }

    private static JsonObject Bindings(CustomReportWorkspaceMark mark) =>
        new(JsonSerializer.SerializeToNode(mark.Bindings, Json)!.AsObject()
            .Where(p => p.Value is not null)
            .Select(p => KeyValuePair.Create(p.Key, p.Value!.DeepClone())));

    [Fact]
    public void Every_cell_mark_and_derived_layer_the_vocabulary_declares_is_a_spec_the_validator_takes_at_every_range()
    {
        var catalogue = ReportCatalogueTests.Catalogue();
        var specs = Specs(catalogue.Workspace, catalogue);

        var refused = specs
            .Select(s => (s.What, Errors: RecordDatasetTests.Validator.Validate(JsonDocument.Parse(s.Spec).RootElement).Errors))
            .Where(s => s.Errors.Count > 0)
            .Select(s => $"{s.What}: {string.Join("; ", s.Errors)}")
            .ToList();

        refused.Should().BeEmpty(string.Join(Environment.NewLine, refused));
        Workspace().Cells.Should().HaveCount(Cells);
        specs.Should().HaveCount(Expected, "the guard counts every spec it builds; change this when the vocabulary changes");
    }

    private const int Cells = 56;
    private const int Expected = 1793;

    [Fact]
    public void Every_cell_names_its_scopes_from_its_dataset_and_fields_and_a_signal_is_permitted_by_its_cells()
    {
        var full = Workspace(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead,
            Scope.HeartRateRead, Scope.StepCountRead, Scope.SleepRead);
        var glucoseOnly = Workspace(Scope.GlucoseRead);

        full.Cells.Should().OnlyContain(c => c.Permitted);
        full.Cells.Single(c => c.Lens == "timeline" && c.Signal == "iob").Scopes.Should().Equal(Scope.GlucoseRead, Scope.TreatmentsRead);
        full.Cells.Single(c => c.Lens == "daily" && c.Signal == "sleep").Scopes.Should().Equal(Scope.SleepRead);
        glucoseOnly.Cells.Single(c => c.Lens == "timeline" && c.Signal == "glucose").Permitted.Should().BeTrue();
        glucoseOnly.Cells.Single(c => c.Lens == "timeline" && c.Signal == "iob").Permitted.Should().BeFalse();
        glucoseOnly.Signals.Single(s => s.Id == "heartRate").Permitted.Should().BeFalse();
        glucoseOnly.Signals.Single(s => s.Id == "glucose").Permitted.Should().BeTrue();
        glucoseOnly.Anchors.Single(a => a.Id == "LowStart").Permitted.Should().BeTrue();
        glucoseOnly.Anchors.Single(a => a.Id == "SleepStart").Scopes.Should().Equal(Scope.GlucoseRead, Scope.SleepRead);
        glucoseOnly.DayKinds.Single(d => d.Id == "exercise").Permitted.Should().BeFalse();
        glucoseOnly.DayKinds.Single(d => d.Id == "lowOvernight").Permitted.Should().BeTrue();
        glucoseOnly.LeaveOuts.Should().OnlyContain(l => !l.Permitted);
        glucoseOnly.LeaveOuts.Single(l => l.Id == "overrides").Scopes.Should().Equal(Scope.TreatmentsRead);
        glucoseOnly.LeaveOuts.Single(l => l.Id == "openLoop").Scopes.Should().Equal(Scope.DevicesRead);
        full.LeaveOuts.Should().OnlyContain(l => l.Permitted);
    }

    /// <summary>
    /// Every one- and two-layer track the cells of one lens make, hued and textured as the runner does them: no two
    /// series on it share a colour, a mark and a texture. Markers and spans carry glyphs and categories, not series.
    /// </summary>
    [Fact]
    public void No_two_series_on_a_track_the_workspace_can_make_look_the_same()
    {
        var workspace = Workspace();
        var drawn = workspace.Cells
            .SelectMany(c => c.Marks.Concat(c.FromStartMarks ?? []).Select(m => (Cell: c, Mark: m)))
            .Where(d => d.Mark.Mark is not ("markers" or "spans"))
            .Select(d => (d.Cell, Layer: ReportTextureTests.Layer($"{d.Cell.Signal}-{d.Mark.Word}", d.Cell.Dataset,
                ReportWire.Parse<ReportMark>(d.Mark.Mark)!.Value, new ReportLayerBindings
                {
                    X = d.Mark.Bindings.X, Y = d.Mark.Bindings.Y, Edges = d.Mark.Bindings.Edges, Series = d.Mark.Bindings.Series,
                    Stacked = d.Mark.Bindings.Stacked,
                })))
            .ToList();
        var tracks = drawn.Select(d => new[] { d.Layer })
            .Concat(drawn.SelectMany(a => drawn.Where(b => b.Cell.Lens == a.Cell.Lens && b.Cell != a.Cell).Select(b => new[] { a.Layer, b.Layer })))
            .ToList();

        var alike = tracks
            .Select(track => ReportSpecRunner.Texture(ReportSpecRunner.Hue(track)))
            .Select(track => track.SelectMany(layer =>
                    (layer.Bindings.Y is { Count: > 0 } y ? y : (layer.Bindings.Edges ?? []).Take(1))
                    .Select(name => layer.Fields.FirstOrDefault(f => f.Name == name)).OfType<ReportField>()
                    .Select(f => (Layer: layer.Id, Field: f.Name,
                        Look: (layer.Color is { } hue ? ReportWire.Name(hue) : f.GlucoseScale ? "glucose" : f.Color is { } c ? ReportWire.Name(c) : "foreground",
                            layer.Mark, f.Texture))))
                .ToList())
            .Where(series => series.Select(s => s.Look).Distinct().Count() != series.Count)
            .Select(series => string.Join(", ", series.Select(s => $"{s.Layer}.{s.Field}")))
            .ToList();

        alike.Should().BeEmpty(string.Join(Environment.NewLine, alike));
        tracks.Should().HaveCountGreaterThan(1000);
    }

    [Fact]
    public void Only_the_anchors_whose_events_corroboration_changes_say_so()
    {
        Workspace().Anchors.Where(a => a.Corroboration).Select(a => a.Id).Should().Equal("UnannouncedRise");
    }

    [Fact]
    public void A_signal_has_the_color_of_its_first_cells_field_wherever_that_field_has_one()
    {
        var registry = ReportSpecValidatorTests.Registry;
        var workspace = Workspace();

        foreach (var signal in workspace.Signals)
        {
            var colored = ReportWorkspace.Table.Where(c => c.Signal == signal.Id)
                .Select(c => ReportWorkspace.LeadColor(c, registry)).OfType<Nocturne.Core.Models.ChartColor>().ToList();
            if (colored.Count > 0)
                signal.Color.Should().Be(ReportWire.Name(colored[0]), signal.Id);
        }
        workspace.Signals.Single(s => s.Id == "sleep").Color.Should().Be("activity-sleep");
    }

    [Fact]
    public void What_the_engine_states_elsewhere_is_derived_not_restated()
    {
        var workspace = Workspace();

        workspace.Anchors.Select(a => a.Id).Should().BeEquivalentTo(Enum.GetNames<Nocturne.Core.Models.LoopAnalysis.EventAnchorKind>());
        workspace.Anchors.Where(a => a.MealNarrowing).Select(a => a.Id).Should().Equal("Meal");
        workspace.Events.Outcomes.Where(o => o.SpreadOnly).Select(o => o.Id).Should().Equal("startGlucose");
        workspace.Events.Along.Should().Equal("startGlucose", "grams", "minutesSinceMeal", "iobAtEvent", "timeOfDay");
        workspace.Events.NoLineAlong.Should().Equal("timeOfDay");
        workspace.Events.WhenAxes.Should().Equal("hour", "weekday");
        workspace.Cells.Single(c => c.Lens == "typical_day" && c.Signal == "glucose").Unit.Should().Be("glucose");
        workspace.Cells.Single(c => c.Lens == "daily" && c.Signal == "ranges").Unit.Should().Be("%");
        var catalogue = ReportCatalogueTests.Catalogue();
        foreach (var leaveOut in workspace.LeaveOuts)
            catalogue.Datasets.Single(d => d.Name == "daily_profile").Parameters.GetProperty("properties").GetProperty(leaveOut.Parameter)
                .GetProperty("default").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        workspace.RangePresets.Max(r => r.Hours).Should().BeLessThanOrEqualTo(catalogue.MaxRangeHours);
    }

    [Fact]
    public void The_vocabulary_carries_ids_and_no_words()
    {
        var json = JsonSerializer.Serialize(Workspace(), Json);

        json.Should().NotContain("\"label\"").And.NotContain("\"description\"").And.NotContain(" ", "an id has no spaces; words are the web's");
    }
}
