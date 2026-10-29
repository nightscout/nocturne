using System.Globalization;
using System.Text;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp;

/// <summary>
/// The text an assistant reads for <c>show_glucose</c>, which is all a host without MCP Apps
/// shows, so it carries every figure the chart does in words.
/// </summary>
internal static class GlucoseSummary
{
    /// <summary>The chart tracks each scope beyond glucose unlocks, in the words the summary uses.</summary>
    internal static readonly IReadOnlyList<(string Scope, string Track)> TrackScopes =
    [
        (Scope.TreatmentsRead, "insulin, carbs and basal"),
        (Scope.DevicesRead, "device events and pump modes"),
        (Scope.TherapyRead, "profiles"),
        (Scope.HeartRateRead, "heart rate"),
        (Scope.StepCountRead, "steps"),
        (Scope.SleepRead, "sleep"),
    ];

    public static IReadOnlyList<string> OmittedTracks(Func<string, bool> hasScope) =>
        [.. TrackScopes.Where(t => !hasScope(t.Scope)).Select(t => t.Track)];

    /// <summary>The scopes behind <see cref="OmittedTracks"/>, for the view's legend.</summary>
    public static IReadOnlyList<string> OmittedScopes(Func<string, bool> hasScope) =>
        [.. TrackScopes.Where(t => !hasScope(t.Scope)).Select(t => t.Scope)];

    public static string Build(
        DashboardChartData data,
        bool useMmol,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string> omittedTracks)
    {
        var text = new StringBuilder();
        var units = useMmol ? "mmol/L" : "mg/dL";

        text.Append(CultureInfo.InvariantCulture,
            $"Glucose from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC");
        text.Append(".").Append('\n');

        var readings = data.GlucoseData;
        if (readings.Count == 0)
        {
            text.Append("No glucose readings in this period.").Append('\n');
        }
        else
        {
            var values = readings.Select(r => r.Sgv).ToList();
            var low = data.Thresholds.Low;
            var high = data.Thresholds.High;
            var inRange = values.Count(v => v >= low && v <= high);
            var below = values.Count(v => v < low);
            var above = values.Count(v => v > high);

            text.Append(CultureInfo.InvariantCulture, $"Readings: {values.Count}.").Append('\n');
            text.Append(CultureInfo.InvariantCulture,
                $"Min {Format(values.Min(), useMmol)}, max {Format(values.Max(), useMmol)}, "
                + $"mean {Format(values.Average(), useMmol)} {units}.").Append('\n');
            text.Append(CultureInfo.InvariantCulture,
                $"Time in range ({Format(low, useMmol)}-{Format(high, useMmol)} {units}): "
                + $"{Percent(inRange, values.Count)}%.").Append('\n');
            text.Append(CultureInfo.InvariantCulture,
                $"Readings below range: {below}. Readings above range: {above}.").Append('\n');
        }

        if (!omittedTracks.Contains(TrackScopes[0].Track))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Boluses: {data.BolusMarkers.Count}. Carb entries: {data.CarbMarkers.Count}.").Append('\n');
        }

        if (omittedTracks.Count > 0)
            text.Append($"Tracks omitted: {string.Join(", ", omittedTracks)} (scope not granted).").Append('\n');

        return text.ToString().TrimEnd();
    }

    internal static string Format(double mgdl, bool useMmol) =>
        GlucoseUnits.Scale(mgdl, useMmol).ToString(useMmol ? "0.0" : "0", CultureInfo.InvariantCulture);

    private static string Percent(int part, int whole) =>
        Math.Round(100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture);
}
