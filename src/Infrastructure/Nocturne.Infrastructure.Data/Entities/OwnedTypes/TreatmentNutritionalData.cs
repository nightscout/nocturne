namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// Nutritional/food data associated with a treatment.
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentNutritionalData
{
    /// <summary>
    /// Protein content in grams
    /// </summary>
    public double? Protein { get; set; }

    /// <summary>
    /// Fat content in grams
    /// </summary>
    public double? Fat { get; set; }

    /// <summary>
    /// Food type
    /// </summary>
    public string? FoodType { get; set; }

    /// <summary>
    /// Carb time offset
    /// </summary>
    public int? CarbTime { get; set; }

    /// <summary>
    /// Carb absorption time in minutes
    /// </summary>
    public int? AbsorptionTime { get; set; }
}
