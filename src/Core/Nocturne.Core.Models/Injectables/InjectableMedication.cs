namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// Represents a user's catalog entry for an injectable medication.
/// This defines the medication's properties and activity profile.
/// </summary>
public class InjectableMedication
{
    /// <summary>
    /// Gets or sets the unique identifier for this medication entry.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the user-defined name for this medication (e.g., "My Humalog", "Tresiba").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category of injectable medication.
    /// </summary>
    public InjectableCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the insulin concentration (U100, U200, U300, etc.).
    /// Defaults to 100 (U100).
    /// </summary>
    public int Concentration { get; set; } = 100;

    /// <summary>
    /// Gets or sets the unit type for this medication (Units for insulin, Milligrams for GLP-1).
    /// </summary>
    public UnitType UnitType { get; set; }

    /// <summary>
    /// Gets or sets the duration of insulin action in hours.
    /// Primarily used for rapid/short-acting insulins.
    /// </summary>
    public double? Dia { get; set; }

    /// <summary>
    /// Gets or sets the onset time in minutes until action begins.
    /// Primarily used for rapid/short-acting insulins.
    /// </summary>
    public double? Onset { get; set; }

    /// <summary>
    /// Gets or sets the peak time in minutes until peak action.
    /// Primarily used for rapid/short-acting insulins.
    /// </summary>
    public double? Peak { get; set; }

    /// <summary>
    /// Gets or sets the duration of action in hours.
    /// Used for long-acting insulins (24, 42, etc.).
    /// </summary>
    public double? Duration { get; set; }

    /// <summary>
    /// Gets or sets the optional default dose for quick entry.
    /// </summary>
    public double? DefaultDose { get; set; }

    /// <summary>
    /// Gets or sets the user's preferred display order.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Gets or sets whether this medication is archived (soft delete).
    /// </summary>
    public bool IsArchived { get; set; }
}
