namespace Nocturne.Core.Models;

using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

/// <summary>
/// COB calculation result with exact 1:1 legacy JavaScript compatibility.
/// </summary>
public class CobResult
{
    public double Cob { get; set; }
    public double? Activity { get; set; }
    public string? Source { get; set; }
    public string? Device { get; set; }

    /// <summary>Whether <see cref="Cob"/> is a device's own total rather than a calculation, zero included.</summary>
    [JsonIgnore]
    public bool ReportedByDevice { get; set; }

    public long? Mills { get; set; }

    /// <summary><see cref="Cob"/> to one decimal, by JavaScript's half-up <c>Math.round</c>, as Nightscout shows it.</summary>
    public double Display => Math.Floor(Cob * 10 + 0.5) / 10;

    public string DisplayLine => $"COB: {Display.ToString(System.Globalization.CultureInfo.InvariantCulture)}g";

    public long? DecayedBy { get; set; }
    public double? IsDecaying { get; set; }
    public double? CarbsHr { get; set; }
    public double? RawCarbImpact { get; set; }
    public CarbIntake? LastCarbs { get; set; }
    public CobResult? TreatmentCOB { get; set; }
}

/// <summary>
/// COB calculation result from the <c>cobCalc</c> function.
/// Exact structure from legacy JavaScript.
/// </summary>
public class CobCalcResult
{
    public double InitialCarbs { get; set; }
    public DateTimeOffset DecayedBy { get; set; }
    public double IsDecaying { get; set; }
    public DateTimeOffset CarbTime { get; set; }
}
