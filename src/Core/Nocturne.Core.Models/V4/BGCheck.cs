using NJsonSchema.Annotations;
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
[JsonSchemaFlatten]
public class BGCheck : V4RecordBase
{
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
}
