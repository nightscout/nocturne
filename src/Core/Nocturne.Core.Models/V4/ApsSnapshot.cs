namespace Nocturne.Core.Models.V4;

/// <summary>
/// Normalized APS algorithm snapshot extracted from DeviceStatus.
/// Captures the common fields across OpenAPS/AAPS/Trio and Loop systems.
/// System-specific algorithm details are preserved in JSON blobs.
/// </summary>
public class ApsSnapshot : IV4Record
{
    public Guid Id { get; set; }
    public long Mills { get; set; }
    public int? UtcOffset { get; set; }
    public string? Device { get; set; }
    public string? App { get; set; }
    public string? DataSource { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? LegacyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Which APS system produced this snapshot
    /// </summary>
    public ApsSystem ApsSystem { get; set; }

    /// <summary>Total insulin on board</summary>
    public double? Iob { get; set; }

    /// <summary>Basal component of IOB</summary>
    public double? BasalIob { get; set; }

    /// <summary>Bolus component of IOB</summary>
    public double? BolusIob { get; set; }

    /// <summary>Carbs on board</summary>
    public double? Cob { get; set; }

    /// <summary>Current blood glucose as seen by the algorithm</summary>
    public double? CurrentBg { get; set; }

    /// <summary>Predicted eventual BG if no further action</summary>
    public double? EventualBg { get; set; }

    /// <summary>Algorithm target BG</summary>
    public double? TargetBg { get; set; }

    /// <summary>Recommended bolus (insulinReq for OpenAPS, recommendedBolus for Loop)</summary>
    public double? RecommendedBolus { get; set; }

    /// <summary>Autosens/dynamic ISF sensitivity ratio</summary>
    public double? SensitivityRatio { get; set; }

    /// <summary>Whether the algorithm's suggestion was enacted (confirmed by pump)</summary>
    public bool Enacted { get; set; }

    /// <summary>Enacted temp basal rate in U/hr</summary>
    public double? EnactedRate { get; set; }

    /// <summary>Enacted temp basal duration in minutes</summary>
    public int? EnactedDuration { get; set; }

    /// <summary>Enacted auto-bolus volume (SMB for OpenAPS, bolusVolume for Loop)</summary>
    public double? EnactedBolusVolume { get; set; }

    /// <summary>Full suggested/recommended JSON blob from the APS system</summary>
    public string? SuggestedJson { get; set; }

    /// <summary>Full enacted JSON blob from the APS system</summary>
    public string? EnactedJson { get; set; }

    /// <summary>Default prediction curve (IOB for OpenAPS, values for Loop) as JSON array</summary>
    public string? PredictedDefaultJson { get; set; }

    /// <summary>IOB-only prediction curve (OpenAPS only) as JSON array</summary>
    public string? PredictedIobJson { get; set; }

    /// <summary>Zero-temp prediction curve (OpenAPS only) as JSON array</summary>
    public string? PredictedZtJson { get; set; }

    /// <summary>COB prediction curve (OpenAPS only) as JSON array</summary>
    public string? PredictedCobJson { get; set; }

    /// <summary>UAM prediction curve (OpenAPS only) as JSON array</summary>
    public string? PredictedUamJson { get; set; }

    /// <summary>Timestamp of prediction start in Unix milliseconds</summary>
    public long? PredictedStartMills { get; set; }
}
