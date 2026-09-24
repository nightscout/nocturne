using Nocturne.Core.Alerts.Native;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>Which <see cref="Nocturne.Core.Contracts.Alerts.IAlertEvaluationEngine"/> implementation serves evaluation.</summary>
internal enum AlertEngineMode
{
    /// <summary>In-process C# evaluators. The default.</summary>
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
/// <c>rust</c>, default <c>managed</c>) into an <see cref="AlertEngineMode"/>, probing the
/// nocturne_alerts native library for the Rust-backed modes.
/// </summary>
/// <remarks>
/// <c>rust</c> makes the native engine authoritative, so a library that fails its probe is a
/// startup failure: serving alerts from the managed engine instead would leave an operator
/// believing the Rust engine is live when it is not. <c>shadow</c> only observes, so it falls
/// back to managed, logged at Error because the comparison it was configured for is not running.
/// Any other value is a startup failure too: a misspelt <c>rust</c> would otherwise run managed.
/// </remarks>
internal static class AlertEngineSelector
{
    public const string ConfigurationKey = "Alerts:Engine";

    /// <exception cref="InvalidOperationException">
    /// <c>rust</c> is configured and the native library fails its probe, or the value is none of
    /// the three.
    /// </exception>
    public static AlertEngineSelection Select(string? configured, Func<NativeProbeResult> nativeProbe, ILogger logger)
    {
        var normalized = (configured ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "" or "managed":
                return new AlertEngineSelection(AlertEngineMode.Managed, "managed");

            case "rust" or "shadow":
            {
                NativeProbeResult probe;
                try
                {
                    probe = nativeProbe();
                }
                catch (Exception ex)
                {
                    if (normalized == "rust")
                        throw new InvalidOperationException(
                            "Alerts:Engine=rust requires the nocturne_alerts native library, and probing it threw", ex);
                    logger.LogError(ex,
                        "Alerts:Engine=shadow requested but probing the nocturne_alerts native library threw; shadow comparison is off and the managed engine serves alerts");
                    return new AlertEngineSelection(AlertEngineMode.Managed, normalized);
                }

                if (!probe.IsAvailable)
                {
                    if (normalized == "rust")
                        throw new InvalidOperationException(
                            $"Alerts:Engine=rust requires the nocturne_alerts native library, which failed its probe: {probe.Failure}");
                    logger.LogError(
                        "Alerts:Engine=shadow requested but the nocturne_alerts native library failed its probe ({ProbeFailure}); shadow comparison is off and the managed engine serves alerts",
                        probe.Failure);
                    return new AlertEngineSelection(AlertEngineMode.Managed, normalized);
                }

                var mode = normalized == "rust" ? AlertEngineMode.Rust : AlertEngineMode.Shadow;
                logger.LogInformation(
                    "Alert evaluation engine: {Mode} (nocturne_alerts {LibraryVersion}, tzdb {TzdbVersion})",
                    normalized, probe.Version, probe.TzdbVersion);
                return new AlertEngineSelection(mode, normalized);
            }

            default:
                throw new InvalidOperationException(
                    $"Unknown Alerts:Engine value '{configured}'; expected managed, shadow or rust");
        }
    }
}
