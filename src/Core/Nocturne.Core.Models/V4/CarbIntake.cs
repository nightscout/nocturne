using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Carbohydrate intake record representing a single carb entry.
/// </summary>
/// <remarks>
/// <para>
/// This is the V4 equivalent of the carbohydrate portion of a legacy <see cref="Treatment"/> record.
/// When a legacy treatment containing both insulin and carbs is decomposed, it produces a
/// <see cref="Bolus"/> and a <see cref="CarbIntake"/> linked by <see cref="IV4Record.CorrelationId"/>.
/// </para>
/// <para>
/// <see cref="AbsorptionTime"/>, when present, overrides the profile default for COB calculations
/// in APS systems such as Loop.
/// </para>
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="Bolus"/>
/// <seealso cref="MealEvent"/>
[JsonSchemaFlatten]
public class CarbIntake : V4RecordBase
{
    /// <summary>
    /// Carbohydrates in grams
    /// </summary>
    public double Carbs { get; set; }

    /// <summary>
    /// APS system sync/deduplication identifier (used by Loop and AAPS)
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Carb time offset in minutes
    /// </summary>
    public double? CarbTime { get; set; }

    /// <summary>
    /// Custom absorption time in minutes (set by Loop and other APS systems).
    /// When present, overrides the profile default for COB calculations.
    /// </summary>
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Fat consumed in grams, when the source reports macros. Native fields replace the
    /// synthesized FPU fake-carb series legacy uploaders emit for Nightscout.
    /// </summary>
    public double? FatGrams { get; set; }

    /// <summary>
    /// Protein consumed in grams, when the source reports macros.
    /// </summary>
    public double? ProteinGrams { get; set; }
}
