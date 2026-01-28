using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// A preset template for a common injectable medication.
/// Used to pre-populate the create form when adding a new medication.
/// </summary>
public class InjectableMedicationPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public InjectableCategory Category { get; set; }

    [JsonPropertyName("concentration")]
    public int Concentration { get; set; } = 100;

    [JsonPropertyName("unitType")]
    public UnitType UnitType { get; set; }

    [JsonPropertyName("dia")]
    public double? Dia { get; set; }

    [JsonPropertyName("onset")]
    public double? Onset { get; set; }

    [JsonPropertyName("peak")]
    public double? Peak { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Returns the full catalog of common injectable medication presets.
    /// </summary>
    public static List<InjectableMedicationPreset> GetAll() =>
    [
        // === Rapid-Acting ===
        new() { Name = "Humalog (Lispro)", Category = InjectableCategory.RapidActing, UnitType = UnitType.Units, Concentration = 100, Dia = 4.0, Onset = 15, Peak = 75 },
        new() { Name = "Humalog U200", Category = InjectableCategory.RapidActing, UnitType = UnitType.Units, Concentration = 200, Dia = 4.0, Onset = 15, Peak = 75 },
        new() { Name = "Novolog (Aspart)", Category = InjectableCategory.RapidActing, UnitType = UnitType.Units, Concentration = 100, Dia = 4.0, Onset = 15, Peak = 75 },
        new() { Name = "Novorapid (Aspart)", Category = InjectableCategory.RapidActing, UnitType = UnitType.Units, Concentration = 100, Dia = 4.0, Onset = 15, Peak = 75 },
        new() { Name = "Apidra (Glulisine)", Category = InjectableCategory.RapidActing, UnitType = UnitType.Units, Concentration = 100, Dia = 4.0, Onset = 15, Peak = 75 },
        new() { Name = "Fiasp", Category = InjectableCategory.RapidActing, UnitType = UnitType.Units, Concentration = 100, Dia = 3.5, Onset = 5, Peak = 60 },

        // === Ultra-Rapid ===
        new() { Name = "Lyumjev", Category = InjectableCategory.UltraRapid, UnitType = UnitType.Units, Concentration = 100, Dia = 3.5, Onset = 5, Peak = 60 },

        // === Short-Acting ===
        new() { Name = "Regular (R)", Category = InjectableCategory.ShortActing, UnitType = UnitType.Units, Concentration = 100, Dia = 6.0, Onset = 30, Peak = 150 },

        // === Intermediate ===
        new() { Name = "NPH", Category = InjectableCategory.Intermediate, UnitType = UnitType.Units, Concentration = 100, Dia = 14.0, Onset = 90, Peak = 480 },

        // === Long-Acting ===
        new() { Name = "Lantus (Glargine)", Category = InjectableCategory.LongActing, UnitType = UnitType.Units, Concentration = 100, Duration = 24 },
        new() { Name = "Toujeo (Glargine)", Category = InjectableCategory.LongActing, UnitType = UnitType.Units, Concentration = 300, Duration = 24 },
        new() { Name = "Basaglar (Glargine)", Category = InjectableCategory.LongActing, UnitType = UnitType.Units, Concentration = 100, Duration = 24 },
        new() { Name = "Levemir (Detemir)", Category = InjectableCategory.LongActing, UnitType = UnitType.Units, Concentration = 100, Duration = 20 },

        // === Ultra-Long ===
        new() { Name = "Tresiba (Degludec)", Category = InjectableCategory.UltraLong, UnitType = UnitType.Units, Concentration = 100, Duration = 42 },
        new() { Name = "Tresiba U200", Category = InjectableCategory.UltraLong, UnitType = UnitType.Units, Concentration = 200, Duration = 42 },

        // === GLP-1 Weekly ===
        new() { Name = "Ozempic (Semaglutide)", Category = InjectableCategory.GLP1Weekly, UnitType = UnitType.Milligrams },
        new() { Name = "Mounjaro (Tirzepatide)", Category = InjectableCategory.GLP1Weekly, UnitType = UnitType.Milligrams },
        new() { Name = "Trulicity (Dulaglutide)", Category = InjectableCategory.GLP1Weekly, UnitType = UnitType.Milligrams },

        // === GLP-1 Daily ===
        new() { Name = "Victoza (Liraglutide)", Category = InjectableCategory.GLP1Daily, UnitType = UnitType.Milligrams },
    ];
}
