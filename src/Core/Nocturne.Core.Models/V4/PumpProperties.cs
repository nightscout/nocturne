namespace Nocturne.Core.Models.V4;

/// <summary>
/// Insulin-pump-specific properties for a <see cref="DeviceCatalogEntry"/>: the parts the pump is
/// made of, what each provides, and how long each lasts.
/// </summary>
/// <seealso cref="DeviceCatalogEntry"/>
/// <seealso cref="DeviceCatalog"/>
public record PumpProperties
{
    /// <summary>The pump's parts, disposable ones first.</summary>
    public required IReadOnlyList<PumpComponent> Components { get; init; }

    /// <summary>
    /// Whether one disposable is both the site and the reservoir, so the pump has no separate
    /// infusion set or reservoir to change.
    /// </summary>
    public bool IsPatch => Components.Any(c =>
        c.Lifecycle == ComponentLifecycle.Disposable
        && c.Provides.Contains(PumpCapability.Site)
        && c.Provides.Contains(PumpCapability.Reservoir));
}
