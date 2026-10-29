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
        text.AppendLine(".");

        var readings = data.GlucoseData;
        if (readings.Count == 0)
        {
            text.AppendLine("No glucose readings in this period.");
        }
        else
        {
            var values = readings.Select(r => r.Sgv).ToList();
            var low = data.Thresholds.Low;
            var high = data.Thresholds.High;
            var inRange = values.Count(v => v >= low && v <= high);
            var below = values.Count(v => v < low);
            var above = values.Count(v => v > high);

            text.AppendLine(CultureInfo.InvariantCulture, $"Readings: {values.Count}.");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Min {Format(values.Min(), useMmol)}, max {Format(values.Max(), useMmol)}, "
                + $"mean {Format(values.Average(), useMmol)} {units}.");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Time in range ({Format(low, useMmol)}-{Format(high, useMmol)} {units}): "
                + $"{Percent(inRange, values.Count)}%.");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Readings below range: {below}. Readings above range: {above}.");
        }

        if (!omittedTracks.Contains(TrackScopes[0].Track))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Boluses: {data.BolusMarkers.Count}. Carb entries: {data.CarbMarkers.Count}.");
        }

        if (omittedTracks.Count > 0)
            text.AppendLine($"Tracks omitted: {string.Join(", ", omittedTracks)} (scope not granted).");

        return text.ToString().TrimEnd();
    }

    internal static string Format(double mgdl, bool useMmol) =>
        GlucoseUnits.Scale(mgdl, useMmol).ToString(useMmol ? "0.0" : "0", CultureInfo.InvariantCulture);

    private static string Percent(int part, int whole) =>
        Math.Round(100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture);
}
