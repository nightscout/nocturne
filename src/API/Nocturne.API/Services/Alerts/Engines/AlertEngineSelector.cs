namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>Which <see cref="Nocturne.Core.Contracts.Alerts.IAlertEvaluationEngine"/> implementation serves evaluation.</summary>
internal enum AlertEngineMode
{
    /// <summary>In-process C# evaluators (the historical engine). The default.</summary>
    Managed,

    /// <summary>Managed is authoritative; the Rust engine runs side-effect-free and divergences are logged.</summary>
    Shadow,

    /// <summary>The Rust engine over FFI is authoritative.</summary>
    Rust,
}

/// <summary>The resolved engine selection, registered as a singleton so the probe and log happen once.</summary>
internal sealed record AlertEngineSelection(AlertEngineMode Mode, string Configured);

/// <summary>
/// Resolves the <c>Alerts:Engine</c> configuration flag (<c>managed</c> | <c>shadow</c> |
/// <c>rust</c>, default <c>managed</c>) into an <see cref="AlertEngineMode"/>. Rust-backed
/// modes degrade gracefully: when the nocturne_alerts native library cannot be loaded
/// (probed once via the version export) the selection falls back to managed with a logged
/// warning instead of failing rule evaluation at runtime.
/// </summary>
internal static class AlertEngineSelector
{
    public const string ConfigurationKey = "Alerts:Engine";

    public static AlertEngineSelection Select(string? configured, Func<bool> nativeProbe, ILogger logger)
    {
        var normalized = (configured ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "" or "managed":
                return new AlertEngineSelection(AlertEngineMode.Managed, "managed");

            case "rust" or "shadow":
            {
                bool available;
                try
                {
                    available = nativeProbe();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Alerts:Engine={Configured} requested but probing the nocturne_alerts native library failed; falling back to the managed engine",
                        normalized);
                    return new AlertEngineSelection(AlertEngineMode.Managed, normalized);
                }

                if (!available)
                {
                    logger.LogWarning(
                        "Alerts:Engine={Configured} requested but the nocturne_alerts native library could not be loaded; falling back to the managed engine",
                        normalized);
                    return new AlertEngineSelection(AlertEngineMode.Managed, normalized);
                }

                var mode = normalized == "rust" ? AlertEngineMode.Rust : AlertEngineMode.Shadow;
                logger.LogInformation("Alert evaluation engine: {Mode}", normalized);
                return new AlertEngineSelection(mode, normalized);
            }

            default:
                logger.LogWarning(
                    "Unknown Alerts:Engine value '{Configured}'; using the managed engine", configured);
                return new AlertEngineSelection(AlertEngineMode.Managed, normalized);
        }
    }
}
