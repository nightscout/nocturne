using System.Text.RegularExpressions;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Interprets pump reservoir display strings (<see cref="PumpSnapshot.ReservoirDisplay"/>).
/// </summary>
public static partial class ReservoirDisplayFormat
{
    /// <summary>
    /// True when the display string marks the numeric reservoir value as a lower bound
    /// rather than an exact reading — e.g. "50+U" from Omnipod pumps, which only report
    /// exact levels below 50 units.
    /// </summary>
    public static bool IsLowerBound(string? display) =>
        display is not null && LowerBoundPattern().IsMatch(display);

    [GeneratedRegex(@"^\s*\d+(\.\d+)?\s*\+")]
    private static partial Regex LowerBoundPattern();
}
