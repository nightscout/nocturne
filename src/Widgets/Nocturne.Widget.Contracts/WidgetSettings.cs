namespace Nocturne.Widget.Contracts;

/// <summary>
/// User-configurable widget preferences, persisted locally and independent of credentials.
/// </summary>
public sealed class WidgetSettings
{
    /// <summary>Glucose display unit. Defaults to mg/dL.</summary>
    public GlucoseUnit Unit { get; set; } = GlucoseUnit.MgDl;
}

/// <summary>
/// Loads and persists user-configurable <see cref="WidgetSettings"/>.
/// </summary>
public interface IWidgetSettingsStore
{
    /// <summary>Returns the current settings, loading from disk on first access.</summary>
    Task<WidgetSettings> GetAsync();

    /// <summary>Persists the supplied settings.</summary>
    Task SaveAsync(WidgetSettings settings);
}
