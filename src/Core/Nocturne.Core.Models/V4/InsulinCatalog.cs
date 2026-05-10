namespace Nocturne.Core.Models.V4;

/// <summary>
/// Static catalog of known insulin formulations with default pharmacokinetic properties.
/// </summary>
public static class InsulinCatalog
{
    private static readonly IReadOnlyList<InsulinFormulation> _formulations =
    [
        // Rapid-acting
        new() { Id = "humalog",      Name = "Humalog (Insulin Lispro)",      Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75,  Curve = "rapid-acting", Concentration = 100 },
        new() { Id = "humalog-u200", Name = "Humalog U200 (Insulin Lispro)", Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75,  Curve = "rapid-acting", Concentration = 200 },
        new() { Id = "admelog",      Name = "Admelog (Insulin Lispro)",      Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75,  Curve = "rapid-acting", Concentration = 100 },
        new() { Id = "novorapid",    Name = "NovoRapid (Insulin Aspart)",    Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75,  Curve = "rapid-acting", Concentration = 100 },
        new() { Id = "apidra",       Name = "Apidra (Insulin Glulisine)",    Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75,  Curve = "rapid-acting", Concentration = 100 },
        new() { Id = "fiasp",        Name = "Fiasp (Faster Aspart)",         Category = InsulinCategory.RapidActing, DefaultDia = 3.5, DefaultPeak = 55,  Curve = "ultra-rapid",  Concentration = 100 },
        new() { Id = "lyumjev",      Name = "Lyumjev (URLi Lispro)",         Category = InsulinCategory.RapidActing, DefaultDia = 3.5, DefaultPeak = 55,  Curve = "ultra-rapid",  Concentration = 100 },
        new() { Id = "lyumjev-u200", Name = "Lyumjev U200 (URLi Lispro)",    Category = InsulinCategory.RapidActing, DefaultDia = 3.5, DefaultPeak = 55,  Curve = "ultra-rapid",  Concentration = 200 },

        // Diluted rapid-acting (pediatric / very low basal)
        new() { Id = "humalog-u10",   Name = "Humalog U10 (Insulin Lispro)",   Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 10 },
        new() { Id = "humalog-u40",   Name = "Humalog U40 (Insulin Lispro)",   Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 40 },
        new() { Id = "humalog-u50",   Name = "Humalog U50 (Insulin Lispro)",   Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 50 },
        new() { Id = "novorapid-u10", Name = "NovoRapid U10 (Insulin Aspart)", Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 10 },
        new() { Id = "novorapid-u40", Name = "NovoRapid U40 (Insulin Aspart)", Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 40 },
        new() { Id = "novorapid-u50", Name = "NovoRapid U50 (Insulin Aspart)", Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 50 },
        new() { Id = "fiasp-u10",     Name = "Fiasp U10 (Faster Aspart)",      Category = InsulinCategory.RapidActing, DefaultDia = 3.5, DefaultPeak = 55, Curve = "ultra-rapid",  Concentration = 10 },

        // Short-acting
        new() { Id = "humulin-r",      Name = "Humulin R (Regular)",      Category = InsulinCategory.ShortActing, DefaultDia = 5.0, DefaultPeak = 90,  Curve = "bilinear", Concentration = 100 },
        new() { Id = "humulin-r-u500", Name = "Humulin R U500 (Regular)", Category = InsulinCategory.ShortActing, DefaultDia = 5.0, DefaultPeak = 90,  Curve = "bilinear", Concentration = 500 },
        new() { Id = "actrapid",       Name = "Actrapid (Regular)",       Category = InsulinCategory.ShortActing, DefaultDia = 5.0, DefaultPeak = 90,  Curve = "bilinear", Concentration = 100 },

        // Long-acting
        new() { Id = "lantus",  Name = "Lantus (Insulin Glargine)", Category = InsulinCategory.LongActing, DefaultDia = 24.0, DefaultPeak = 480, Curve = "bilinear", Concentration = 100 },
        new() { Id = "levemir", Name = "Levemir (Insulin Detemir)", Category = InsulinCategory.LongActing, DefaultDia = 18.0, DefaultPeak = 420, Curve = "bilinear", Concentration = 100 },

        // Ultra-long-acting
        new() { Id = "tresiba",      Name = "Tresiba (Insulin Degludec)",      Category = InsulinCategory.UltraLongActing, DefaultDia = 42.0, DefaultPeak = 660, Curve = "bilinear", Concentration = 100 },
        new() { Id = "tresiba-u200", Name = "Tresiba U200 (Insulin Degludec)", Category = InsulinCategory.UltraLongActing, DefaultDia = 42.0, DefaultPeak = 660, Curve = "bilinear", Concentration = 200 },
        new() { Id = "toujeo",       Name = "Toujeo (Insulin Glargine)",       Category = InsulinCategory.UltraLongActing, DefaultDia = 36.0, DefaultPeak = 720, Curve = "bilinear", Concentration = 300 },

        // Custom
        new() { Id = "custom", Name = "Custom", Category = InsulinCategory.RapidActing, DefaultDia = 4.0, DefaultPeak = 75, Curve = "rapid-acting", Concentration = 100 },
    ];

    /// <summary>
    /// Returns all known insulin formulations across all categories.
    /// </summary>
    /// <returns>A read-only list of all <see cref="InsulinFormulation"/> records.</returns>
    public static IReadOnlyList<InsulinFormulation> GetAll() => _formulations;

    /// <summary>
    /// Looks up an insulin formulation by its unique identifier.
    /// </summary>
    /// <param name="id">The catalog identifier (e.g., "humalog", "fiasp", "lantus").</param>
    /// <returns>The matching <see cref="InsulinFormulation"/>, or <c>null</c> if not found.</returns>
    public static InsulinFormulation? GetById(string id) =>
        _formulations.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// Returns all formulations that belong to the specified <see cref="InsulinCategory"/>.
    /// </summary>
    /// <param name="category">The pharmacokinetic category to filter by.</param>
    /// <returns>A read-only list of <see cref="InsulinFormulation"/> records in the given category.</returns>
    public static IReadOnlyList<InsulinFormulation> GetByCategory(InsulinCategory category) =>
        _formulations.Where(f => f.Category == category).ToList();
}
