namespace Nocturne.Core.Models.V4;

/// <summary>
/// Carbohydrate intake record
/// </summary>
public class CarbIntake
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp in Unix milliseconds
    /// </summary>
    public long Mills { get; set; }

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
    /// Protein in grams
    /// </summary>
    public double? Protein { get; set; }

    /// <summary>
    /// Fat in grams
    /// </summary>
    public double? Fat { get; set; }

    /// <summary>
    /// User-specified food type (freeform)
    /// </summary>
    public string? FoodType { get; set; }

    /// <summary>
    /// Carbohydrate absorption time in minutes
    /// </summary>
    public double? AbsorptionTime { get; set; }

    /// <summary>
    /// APS system sync/deduplication identifier (used by Loop and AAPS)
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Carb time offset in minutes
    /// </summary>
    public double? CarbTime { get; set; }
}
