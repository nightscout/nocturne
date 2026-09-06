using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting an APS loop algorithm snapshot via the V4 API.
/// Mirrors <see cref="ApsSnapshot"/> minus the server-assigned fields
/// (Id, CreatedAt, ModifiedAt, LegacyId, Mills, device FKs).
/// </summary>
/// <remarks>
/// Records carrying both <see cref="DataSource"/> and <see cref="SyncIdentifier"/> are upserted:
/// a row already matched by that pair is updated in place, so uploader retries of the same loop
/// cycle stay idempotent. <see cref="CorrelationId"/> ties one loop cycle's APS, pump, and
/// uploader snapshots together.
/// </remarks>
/// <seealso cref="Nocturne.API.Controllers.V4.Devices.ApsSnapshotController"/>
public class UpsertApsSnapshotRequest : IBulkUpsertRequest
{
    /// <summary>
    /// When the loop decision was made (e.g. the determination's deliverAt).
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that produced this snapshot.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier; required when SyncIdentifier is supplied.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier. When paired with DataSource, re-uploading the same
    /// snapshot updates the existing record in place rather than creating a duplicate.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Correlation identifier shared by the APS, pump, and uploader snapshots of one loop cycle.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Which AID algorithm produced this snapshot.
    /// </summary>
    public AidAlgorithm AidAlgorithm { get; set; }

    /// <summary>
    /// Algorithm version string (e.g. Trio app version).
    /// </summary>
    public string? AidVersion { get; set; }

    /// <summary>Total insulin on board</summary>
    public double? Iob { get; set; }

    /// <summary>Basal component of IOB</summary>
    public double? BasalIob { get; set; }

    /// <summary>Bolus component of IOB</summary>
    public double? BolusIob { get; set; }

    /// <summary>Carbs on board</summary>
    public double? Cob { get; set; }

    /// <summary>Current blood glucose as seen by the algorithm (mg/dL)</summary>
    public double? CurrentBg { get; set; }

    /// <summary>Predicted eventual BG if no further action (mg/dL)</summary>
    public double? EventualBg { get; set; }

    /// <summary>Algorithm target BG (mg/dL)</summary>
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

    /// <summary>Timestamp the prediction curves start from</summary>
    public DateTimeOffset? PredictedStartTimestamp { get; set; }

    /// <summary>Full serialized Loop status object for round-trip fidelity</summary>
    public string? LoopJson { get; set; }

    /// <summary>Catch-all for fields not mapped to dedicated columns</summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
