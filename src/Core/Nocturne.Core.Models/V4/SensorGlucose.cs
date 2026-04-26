namespace Nocturne.Core.Models.V4;

/// <summary>
/// Continuous glucose monitor (CGM) reading representing a single sensor glucose value.
/// </summary>
/// <remarks>
/// <para>
/// This is the V4 equivalent of the legacy <see cref="Entry"/> model. Each CGM reading is
/// stored as its own record rather than being multiplexed through the entries collection.
/// </para>
/// <para>
/// <see cref="Mmol"/> is computed from <see cref="Mgdl"/> using the standard conversion factor
/// (18.0182). <see cref="Trend"/> is computed by casting <see cref="Direction"/> to its integer
/// equivalent, providing a 1:1 mapping between <see cref="GlucoseDirection"/> and <see cref="GlucoseTrend"/>.
/// </para>
/// </remarks>
/// <seealso cref="Entry"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="GlucoseDirection"/>
/// <seealso cref="GlucoseTrend"/>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="BGCheck"/>
public class SensorGlucose : IV4Record
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
    /// Glucose value in mmol/L (computed from <see cref="Mgdl"/>).
    /// </summary>
    /// <remarks>
    /// Computed as <c>Mgdl / 18.0182</c>. The mg/dL value is the source of truth.
    /// </remarks>
    public double Mmol => Mgdl / 18.0182;

    /// <summary>
    /// CGM trend arrow direction.
    /// </summary>
    /// <seealso cref="GlucoseDirection"/>
    /// <seealso cref="Trend"/>
    public GlucoseDirection? Direction { get; set; }

    /// <summary>
    /// Numeric trend value computed from <see cref="Direction"/> via a 1:1 integer cast.
    /// </summary>
    /// <remarks>
    /// Computed as <c>(GlucoseTrend)(int)Direction.Value</c>. Returns <c>null</c> when
    /// <see cref="Direction"/> is not set. The <see cref="GlucoseTrend"/> enum values
    /// mirror <see cref="GlucoseDirection"/> by ordinal.
    /// </remarks>
    public GlucoseTrend? Trend => Direction.HasValue ? (GlucoseTrend)(int)Direction.Value : null;

    /// <summary>
    /// Rate of glucose change in mg/dL per minute
    /// </summary>
    public double? TrendRate { get; set; }

    /// <summary>
    /// Signal noise level (0-4)
    /// </summary>
    public int? Noise { get; set; }

    /// <summary>
    /// Raw filtered sensor value (scaled ADC)
    /// </summary>
    public double? Filtered { get; set; }

    /// <summary>
    /// Raw unfiltered sensor value (scaled ADC)
    /// </summary>
    public double? Unfiltered { get; set; }

    /// <summary>
    /// Glucose delta in mg/dL over the last 5 minutes
    /// </summary>
    public double? Delta { get; set; }

    /// <summary>
    /// Whether this glucose value has been algorithmically smoothed or is raw sensor output.
    /// <c>null</c> when the uploader did not declare.
    /// </summary>
    public GlucoseProcessing? GlucoseProcessing { get; set; }

    /// <summary>
    /// Smoothed glucose value in mg/dL, when known.
    /// </summary>
    public double? SmoothedMgdl { get; set; }

    /// <summary>
    /// Smoothed glucose value in mmol/L (computed from <see cref="SmoothedMgdl"/>).
    /// </summary>
    public double? SmoothedMmol => SmoothedMgdl.HasValue ? SmoothedMgdl.Value / 18.0182 : null;

    /// <summary>
    /// Unsmoothed (raw) glucose value in mg/dL, when known.
    /// </summary>
    public double? UnsmoothedMgdl { get; set; }

    /// <summary>
    /// Unsmoothed glucose value in mmol/L (computed from <see cref="UnsmoothedMgdl"/>).
    /// </summary>
    public double? UnsmoothedMmol => UnsmoothedMgdl.HasValue ? UnsmoothedMgdl.Value / 18.0182 : null;

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
