namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// Status of an insulin pen or vial in inventory.
/// </summary>
public enum PenVialStatus
{
    /// <summary>
    /// Pen/vial has not been opened yet
    /// </summary>
    Unopened,

    /// <summary>
    /// Pen/vial is currently in use
    /// </summary>
    Active,

    /// <summary>
    /// Pen/vial is empty
    /// </summary>
    Empty,

    /// <summary>
    /// Pen/vial has expired
    /// </summary>
    Expired
}
