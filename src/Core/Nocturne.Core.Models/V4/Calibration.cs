using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// CGM sensor calibration record containing slope, intercept, and scale values.
/// </summary>
/// <remarks>
/// Calibrations are extracted from legacy <see cref="Entry"/> records that carried calibration
/// data (typically from older CGM systems like Dexcom G4/G5 via xDrip). Modern factory-calibrated
/// CGMs (G6, G7, Libre) generally do not produce calibration records.
/// </remarks>
/// <seealso cref="Entry"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="SensorGlucose"/>
[JsonSchemaFlatten]
public class Calibration : V4RecordBase
{
    /// <summary>
    /// Calibration slope value
    /// </summary>
    public double? Slope { get; set; }

    /// <summary>
    /// Calibration intercept value
    /// </summary>
    public double? Intercept { get; set; }

    /// <summary>
    /// Calibration scale value
    /// </summary>
    public double? Scale { get; set; }
}
