namespace Nocturne.Core.Models.V4;

/// <summary>
/// Normalized APS algorithm snapshot extracted from a legacy <see cref="DeviceStatus"/> record.
/// Captures the common fields across OpenAPS/AAPS/Trio and Loop systems.
/// System-specific algorithm details are preserved in JSON blobs.
/// </summary>
/// <remarks>
/// <para>
/// A single legacy <see cref="DeviceStatus"/> is decomposed into up to three V4 records:
/// an <see cref="ApsSnapshot"/>, a <see cref="PumpSnapshot"/>, and an <see cref="UploaderSnapshot"/>,
/// all sharing the same <see cref="IV4Record.CorrelationId"/>.
/// </para>
/// <para>
/// Prediction curves (<see cref="PredictedDefaultJson"/>, <see cref="PredictedIobJson"/>, etc.)
/// are stored as raw JSON arrays of glucose values. <see cref="PredictedStartTimestamp"/> marks
/// the time origin for these arrays; <see cref="PredictedStartMills"/> is its computed
/// Unix-millisecond equivalent.
/// </para>
/// </remarks>
/// <seealso cref="DeviceStatus"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="PumpSnapshot"/>
/// <seealso cref="UploaderSnapshot"/>
/// <seealso cref="AidAlgorithm"/>
/// <seealso cref="Bolus"/>
/// <seealso cref="TempBasal"/>
public class ApsSnapshot : IV4Record
{
    /// <inheritdoc />
    public Guid Id { get; set; }

    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <inheritdoc />
    public long Mills => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <inheritdoc />
    public int? UtcOffset { get; set; }

    /// <inheritdoc />
    public string? Device { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="Device"/> table.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> table.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <inheritdoc />
    public string? App { get; set; }

    /// <inheritdoc />
    public string? DataSource { get; set; }

    /// <inheritdoc />
    public Guid? CorrelationId { get; set; }

    /// <inheritdoc />
    public string? LegacyId { get; set; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Which AID algorithm produced this snapshot.
    /// </summary>
    /// <seealso cref="V4.AidAlgorithm"/>
    public AidAlgorithm AidAlgorithm { get; set; }

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

    /// <summary>Timestamp of prediction start as UTC DateTime</summary>
    public DateTime? PredictedStartTimestamp { get; set; }

    /// <summary>
    /// Timestamp of prediction start in Unix milliseconds, computed from <see cref="PredictedStartTimestamp"/>.
    /// Returns <c>null</c> when <see cref="PredictedStartTimestamp"/> is not set.
    /// </summary>
    public long? PredictedStartMills => PredictedStartTimestamp.HasValue ? new DateTimeOffset(PredictedStartTimestamp.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null;

    /// <summary>Full serialized Loop status object for round-trip fidelity.</summary>
    public string? LoopJson { get; set; }

    /// <summary>Algorithm version string (e.g. Trio app version).</summary>
    public string? AidVersion { get; set; }

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
