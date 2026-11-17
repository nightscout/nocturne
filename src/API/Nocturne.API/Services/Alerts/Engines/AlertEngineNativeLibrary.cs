using Nocturne.Core.Alerts.Native;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>The nocturne_alerts native library the alert engine runs on, as its probe found it.</summary>
/// <remarks>
/// Alerts evaluate nowhere else, so a library that fails its probe stops the host at startup
/// rather than leaving it serving without alerts.
/// </remarks>
internal sealed record AlertEngineNativeLibrary(string? Version, string? TzdbVersion)
{
    /// <exception cref="InvalidOperationException">The probe threw or failed.</exception>
    public static AlertEngineNativeLibrary Load(Func<NativeProbeResult> probe, ILogger logger)
    {
        NativeProbeResult result;
        try
        {
            result = probe();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Alerts require the nocturne_alerts native library, and probing it threw", ex);
        }

        if (!result.IsAvailable)
            throw new InvalidOperationException(
                $"Alerts require the nocturne_alerts native library, which failed its probe: {result.Failure}");

        logger.LogInformation(
            "Alert engine: nocturne_alerts {LibraryVersion}, tzdb {TzdbVersion}", result.Version, result.TzdbVersion);
        return new AlertEngineNativeLibrary(result.Version, result.TzdbVersion);
    }
}
