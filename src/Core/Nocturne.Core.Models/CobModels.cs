namespace Nocturne.Core.Models;

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
    public long? Mills { get; set; }
    public string? Display { get; set; }
    public string? DisplayLine { get; set; }
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

/// <summary>
/// COB contribution calculated for a single <see cref="CarbIntake"/>.
/// </summary>
public class CarbCobContribution
{
    public double CobContrib { get; set; }
    public double ActivityContrib { get; set; }
    public long? DecayedBy { get; set; }
    public bool IsDecaying { get; set; }
}
