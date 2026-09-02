using NJsonSchema.Annotations;
using Nocturne.Core.Constants;

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
/// <see cref="Mmol"/> is computed from <see cref="Mgdl"/> using
/// <see cref="GlucoseConstants.MgdlPerMmol"/>. <see cref="Trend"/> is computed by casting
/// <see cref="Direction"/> to its integer equivalent, providing a 1:1 mapping between
/// <see cref="GlucoseDirection"/> and <see cref="GlucoseTrend"/>.
/// </para>
/// </remarks>
/// <seealso cref="Entry"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="GlucoseDirection"/>
/// <seealso cref="GlucoseTrend"/>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="BGCheck"/>
[JsonSchemaFlatten]
public class SensorGlucose : V4RecordBase, IDeviceAttributed
{
    /// <summary>
    /// FK to the patient's registered CGM device (null if not yet resolved)
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Stable per-source identifier. Records matched on (DataSource, SyncIdentifier) are updated in
    /// place on re-import (unlike the insert-only <see cref="LegacyId"/>), so a re-corrected timestamp
    /// moves the existing row instead of duplicating it.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose value in mmol/L (computed from <see cref="Mgdl"/>).
    /// </summary>
    /// <remarks>
    /// The mg/dL value is the source of truth.
    /// </remarks>
    public double Mmol => Mgdl / GlucoseConstants.MgdlPerMmol;

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
    public double? SmoothedMmol =>
        SmoothedMgdl.HasValue ? SmoothedMgdl.Value / GlucoseConstants.MgdlPerMmol : null;

    /// <summary>
    /// Unsmoothed (raw) glucose value in mg/dL, when known.
    /// </summary>
    public double? UnsmoothedMgdl { get; set; }

    /// <summary>
    /// Unsmoothed glucose value in mmol/L (computed from <see cref="UnsmoothedMgdl"/>).
    /// </summary>
    public double? UnsmoothedMmol =>
        UnsmoothedMgdl.HasValue ? UnsmoothedMgdl.Value / GlucoseConstants.MgdlPerMmol : null;
}
