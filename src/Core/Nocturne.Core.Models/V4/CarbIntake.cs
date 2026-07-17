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
public class CarbIntake : IV4Record
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
    /// Device identifier that recorded this intake
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this intake
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

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
