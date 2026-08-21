using Nocturne.Core.Constants;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Blood glucose check record (finger stick or sensor check) with user-entered glucose value.
/// </summary>
/// <remarks>
/// <para>
/// This is the V4 equivalent of a legacy <see cref="Treatment"/> with type <c>mbg</c>
/// (manual blood glucose). The <see cref="Glucose"/> value and <see cref="Units"/> are the
/// source of truth as entered by the user; <see cref="Mgdl"/> and <see cref="Mmol"/> are
/// computed properties that normalize to both unit systems.
/// </para>
/// <para>
/// <see cref="Mgdl"/> and <see cref="Mmol"/> convert through
/// <see cref="GlucoseConstants.MgdlPerMmol"/> when <see cref="Units"/> is not already the
/// requested unit.
/// </para>
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="GlucoseType"/>
/// <seealso cref="GlucoseUnit"/>
public class BGCheck : IV4Record
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp as UTC DateTime
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Unix milliseconds (computed from Timestamp for v1/v3 compatibility)
    /// </summary>
    public long Mills => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that performed this check
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this check
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    public string? LegacyId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Glucose value as entered by the user (source of truth)
    /// </summary>
    public double Glucose { get; set; }

    /// <summary>
    /// Source type of the glucose reading (<see cref="V4.GlucoseType.Finger"/> or <see cref="V4.GlucoseType.Sensor"/>).
    /// </summary>
    public GlucoseType? GlucoseType { get; set; }

    /// <summary>
    /// Unit of measurement for the <see cref="Glucose"/> value (source of truth).
    /// </summary>
    public GlucoseUnit? Units { get; set; }

    /// <summary>
    /// Glucose in mg/dL, computed from <see cref="Glucose"/> and <see cref="Units"/>.
    /// </summary>
    public double Mgdl =>
        Units == GlucoseUnit.Mmol ? Glucose * GlucoseConstants.MgdlPerMmol : Glucose;

    /// <summary>
    /// Glucose in mmol/L, computed from <see cref="Glucose"/> and <see cref="Units"/>.
    /// </summary>
    public double Mmol =>
        Units == GlucoseUnit.Mmol ? Glucose : Glucose / GlucoseConstants.MgdlPerMmol;

    /// <summary>
    /// APS system sync/deduplication identifier (used by Loop and AAPS)
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
