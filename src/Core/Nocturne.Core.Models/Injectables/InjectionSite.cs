namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// Anatomical site where an injection was administered.
/// </summary>
public enum InjectionSite
{
    /// <summary>
    /// Abdomen (unspecified side)
    /// </summary>
    Abdomen,

    /// <summary>
    /// Left side of abdomen
    /// </summary>
    AbdomenLeft,

    /// <summary>
    /// Right side of abdomen
    /// </summary>
    AbdomenRight,

    /// <summary>
    /// Left thigh
    /// </summary>
    ThighLeft,

    /// <summary>
    /// Right thigh
    /// </summary>
    ThighRight,

    /// <summary>
    /// Left arm
    /// </summary>
    ArmLeft,

    /// <summary>
    /// Right arm
    /// </summary>
    ArmRight,

    /// <summary>
    /// Buttock
    /// </summary>
    Buttock,

    /// <summary>
    /// Other site
    /// </summary>
    Other
}
