using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// What each part of a clock face reads, and the scope that read takes. Both sides of a face use it:
/// saving a face that shows a part takes that part's scope, and a face serves a part only while its
/// owner holds the scope. Anyone holding the face's link sees what it serves.
/// </summary>
/// <remarks>
/// The latest reading, its delta, arrow and age are the face's base, served to every face as
/// <c>ClockFacesController.GetGlucose</c> does, so they need nothing here.
/// </remarks>
public static class ClockFaceReadScopes
{
    /// <summary>A part of a face and the scope it reads.</summary>
    /// <param name="Part">Names the part, so a saved face keeps a part its owner can no longer add.</param>
    /// <param name="Scope">The scope reading the part takes.</param>
    public sealed record Requirement(string Part, string Scope);

    /// <summary>The requirements of one element.</summary>
    public static IReadOnlyList<Requirement> Of(ClockElement element) => element.Type switch
    {
        "summary" => [new("summary", Scope.GlucoseRead)],
        "iob" => [new("iob", Scope.TreatmentsRead)],
        "cob" => [new("cob", Scope.TreatmentsRead)],
        "basal" => [new("basal", Scope.TreatmentsRead)],
        "forecast" => [new("forecast", Scope.GlucoseRead), new("forecast", Scope.TreatmentsRead)],
        "tracker" or "trackers" => [new("trackers", Scope.AlertsRead)],
        "chart" => OfChart(element.ChartConfig ?? new ClockChartConfig()),
        _ => [],
    };

    /// <summary>The requirements of a chart element, by the tracks it draws.</summary>
    public static IReadOnlyList<Requirement> OfChart(ClockChartConfig chart)
    {
        List<Requirement> required = [new("chart.glucose", Scope.GlucoseRead)];
        if (DrawsTreatments(chart))
            required.Add(new("chart.treatments", Scope.TreatmentsRead));
        if (chart.ShowDeviceEvents)
            required.Add(new("chart.devices", Scope.DevicesRead));
        if (chart.ShowTrackers)
            required.Add(new("chart.trackers", Scope.AlertsRead));
        return required;
    }

    /// <summary>Whether the chart draws anything computed from treatments, a forecast among them.</summary>
    public static bool DrawsTreatments(ClockChartConfig chart) =>
        chart.ShowBolus || chart.ShowCarbs || chart.ShowIob || chart.ShowCob || chart.ShowBasal
        || chart.ShowPredictions;

    /// <summary>Every requirement of the face: its elements' and its alert settings'.</summary>
    public static IReadOnlyList<Requirement> Of(ClockFaceConfig? config)
    {
        if (config is null)
            return [];

        var required = config.Rows.SelectMany(r => r.Elements).SelectMany(Of).ToList();
        if (config.Settings?.ShowAlerts == true)
            required.Add(new("showAlerts", Scope.AlertsRead));
        if (config.Settings?.AllowMarkSeen == true)
            required.Add(new("allowMarkSeen", Scope.AlertsRead));
        return required;
    }

    /// <summary>The element types whose values take a scope; the face's base takes none.</summary>
    private static readonly string[] DataTypes = ["summary", "iob", "cob", "basal", "forecast", "tracker", "trackers"];

    /// <summary>Each chart flag, as the face's JSON names it, alone on a chart that draws nothing else.</summary>
    private static readonly (string Feature, ClockChartConfig Chart)[] ChartFeatures =
    [
        ("showBolus", ChartWith(c => c.ShowBolus = true)),
        ("showCarbs", ChartWith(c => c.ShowCarbs = true)),
        ("showIob", ChartWith(c => c.ShowIob = true)),
        ("showCob", ChartWith(c => c.ShowCob = true)),
        ("showBasal", ChartWith(c => c.ShowBasal = true)),
        ("showPredictions", ChartWith(c => c.ShowPredictions = true)),
        ("showDeviceEvents", ChartWith(c => c.ShowDeviceEvents = true)),
        ("showTrackers", ChartWith(c => c.ShowTrackers = true)),
    ];

    private static ClockChartConfig ChartWith(Action<ClockChartConfig> flag)
    {
        var chart = new ClockChartConfig { ShowBolus = false, ShowCarbs = false };
        flag(chart);
        return chart;
    }

    /// <summary>
    /// For every part that takes a scope, the scope a caller holding <paramref name="held"/> lacks to
    /// add it, as <see cref="MissingFor"/> would refuse a face that adds it.
    /// </summary>
    public static IEnumerable<ClockPartAccessDto> Access(IReadOnlySet<string> held)
    {
        string? Missing(IEnumerable<Requirement> required) =>
            required.Select(r => r.Scope).FirstOrDefault(scope => !Scope.Satisfies(held, scope));

        foreach (var type in DataTypes)
            yield return new ClockPartAccessDto { Type = type, MissingScope = Missing(Of(new ClockElement { Type = type })) };

        yield return new ClockPartAccessDto { Type = "chart", MissingScope = Missing(OfChart(ChartWith(_ => { }))) };
        foreach (var (feature, chart) in ChartFeatures)
            yield return new ClockPartAccessDto { Type = "chart", Feature = feature, MissingScope = Missing(OfChart(chart)) };
    }

    /// <summary>Whether a reader holding <paramref name="held"/> may be served the element.</summary>
    public static bool Allows(IReadOnlySet<string> held, ClockElement element) =>
        Of(element).All(r => Scope.Satisfies(held, r.Scope));

    /// <summary>
    /// The first scope <paramref name="after"/> needs that <paramref name="held"/> lacks, for a part
    /// <paramref name="before"/> did not already have; null when the save may go ahead. Keeping a part
    /// the face already showed is not adding it, and the face stops serving it once its owner lacks
    /// the scope.
    /// </summary>
    public static string? MissingFor(ClockFaceConfig? after, ClockFaceConfig? before, IReadOnlySet<string> held)
    {
        var kept = Of(before).Select(r => r.Part).ToHashSet();
        return Of(after)
            .Where(r => !kept.Contains(r.Part) && !Scope.Satisfies(held, r.Scope))
            .Select(r => r.Scope)
            .FirstOrDefault();
    }
}
