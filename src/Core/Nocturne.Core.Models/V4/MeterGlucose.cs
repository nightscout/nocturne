using NJsonSchema.Annotations;
using Nocturne.Core.Constants;

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
/// <see cref="Mmol"/> is computed from <see cref="Mgdl"/> using
/// <see cref="GlucoseConstants.MgdlPerMmol"/>. <see cref="Mgdl"/> is always the source of truth.
/// </para>
/// </remarks>
/// <seealso cref="Entry"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="BGCheck"/>
/// <seealso cref="SensorGlucose"/>
[JsonSchemaFlatten]
public class MeterGlucose : V4RecordBase, IDeviceAttributed
{
    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> (meter) this reading is attributed to.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose value in mmol/L, computed from <see cref="Mgdl"/>.
    /// </summary>
    /// <remarks>
    /// The mg/dL value is the source of truth.
    /// </remarks>
    public double Mmol => Mgdl / GlucoseConstants.MgdlPerMmol;
}
