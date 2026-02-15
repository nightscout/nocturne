namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// Loop remote command data associated with a treatment.
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentLoopData
{
    /// <summary>
    /// Remote carb entry amount in grams (for Loop remote commands)
    /// </summary>
    public double? RemoteCarbs { get; set; }

    /// <summary>
    /// Remote carb absorption time in hours (for Loop remote commands)
    /// </summary>
    public double? RemoteAbsorption { get; set; }

    /// <summary>
    /// Remote bolus amount in units (for Loop remote commands)
    /// </summary>
    public double? RemoteBolus { get; set; }

    /// <summary>
    /// One-time password for secure remote operations
    /// </summary>
    public string? Otp { get; set; }

    /// <summary>
    /// Display name for override reason
    /// </summary>
    public string? ReasonDisplay { get; set; }
}
