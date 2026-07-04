namespace Nocturne.Core.Models.V4;

/// <summary>
/// Blood glucose meter reading from a dedicated glucometer device.
/// </summary>
/// <remarks>
/// <para>
/// Corresponds to legacy <see cref="Entry"/> records with type <c>mbg</c> or <c>cal</c> that carry
/// a meter glucose value. Unlike <see cref="BGCheck"/> (which is user-entered), <see cref="MeterGlucose"/>
/// is sourced directly from the meter via upload (e.g., through a connector or xDrip).
/// </para>
/// <para>
/// <see cref="Mmol"/> is computed from <see cref="Mgdl"/> using the standard conversion factor
/// (18.0182). <see cref="Mgdl"/> is always the source of truth.
/// </para>
/// </remarks>
/// <seealso cref="Entry"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="BGCheck"/>
/// <seealso cref="SensorGlucose"/>
public class MeterGlucose : IV4Record, IDeviceAttributed
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
    /// Device identifier that produced this reading
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this reading
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
    /// Foreign key to the <see cref="PatientDevice"/> (meter) this reading is attributed to.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose value in mmol/L, computed from <see cref="Mgdl"/>.
    /// </summary>
    /// <remarks>
    /// Computed as <c>Mgdl / 18.0182</c>. The mg/dL value is the source of truth.
    /// </remarks>
    public double Mmol => Mgdl / 18.0182;

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
