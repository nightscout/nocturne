namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// Category of injectable medication based on action profile.
/// </summary>
public enum InjectableCategory
{
    /// <summary>
    /// Rapid-acting insulin (Humalog, Novolog/Novorapid, Fiasp, Apidra)
    /// </summary>
    RapidActing,

    /// <summary>
    /// Ultra-rapid acting insulin (Lyumjev, Afrezza)
    /// </summary>
    UltraRapid,

    /// <summary>
    /// Short-acting insulin (Regular/R)
    /// </summary>
    ShortActing,

    /// <summary>
    /// Intermediate-acting insulin (NPH)
    /// </summary>
    Intermediate,

    /// <summary>
    /// Long-acting insulin (Lantus, Levemir, Basaglar)
    /// </summary>
    LongActing,

    /// <summary>
    /// Ultra-long acting insulin (Tresiba, Toujeo)
    /// </summary>
    UltraLong,

    /// <summary>
    /// Daily GLP-1 agonist (Victoza)
    /// </summary>
    GLP1Daily,

    /// <summary>
    /// Weekly GLP-1 agonist (Ozempic, Mounjaro, Trulicity)
    /// </summary>
    GLP1Weekly,

    /// <summary>
    /// Other injectable medication (future-proofing)
    /// </summary>
    Other
}
