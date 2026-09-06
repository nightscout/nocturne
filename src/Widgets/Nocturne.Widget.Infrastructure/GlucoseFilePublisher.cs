using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure;

/// <summary>
/// Writes the raw V4 summary JSON to <c>%LOCALAPPDATA%\Nocturne\glucose.json</c> via a temp-file +
/// atomic rename, mirroring the desktop companion's writer so the taskbar mod can read from either
/// source. For the packaged widget, Windows redirects this AppData write into the package container;
/// the taskbar discovers that redirected path separately and uses whichever source is freshest.
/// </summary>
public sealed class GlucoseFilePublisher : IGlucoseFilePublisher
{
    private readonly ILogger<GlucoseFilePublisher> _logger;

    /// <summary>Initializes the publisher with a logger.</summary>
    public GlucoseFilePublisher(ILogger<GlucoseFilePublisher> logger) => _logger = logger;

    /// <summary>
    /// Returns <c>%LOCALAPPDATA%\Nocturne\glucose.json</c>, falling back to
    /// <c>%USERPROFILE%\AppData\Local\Nocturne\glucose.json</c> when LOCALAPPDATA is unset — the same
    /// logical path the companion uses. Windows may redirect this AppData write into the package
    /// container for the packaged widget; the taskbar reads both the real path and the container
    /// location, so the feature works whichever way the write lands.
    /// </summary>
    public static string GlucoseFilePath()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(localAppData))
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
            localAppData = Path.Combine(userProfile, "AppData", "Local");
        }

        return Path.Combine(localAppData, "Nocturne", "glucose.json");
    }

    /// <inheritdoc />
    public async Task PublishAsync(string rawSummaryJson)
    {
        if (string.IsNullOrEmpty(rawSummaryJson))
        {
            return;
        }

        var target = GlucoseFilePath();
        // Per-write unique temp so the rename is the only shared operation: this never collides with
        // the companion (or a second in-process publish) racing on the same directory — both of which
        // happen when the OS does not redirect the widget's write into its package container.
        var tmp = $"{target}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Temp-file + atomic rename so a reader never observes a half-written file.
            var bytes = Encoding.UTF8.GetBytes(rawSummaryJson);
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, target, overwrite: true);

            _logger.LogDebug("Published glucose summary to {Path} ({Bytes} bytes)", target, bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish glucose file");
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort temp cleanup; ignore.
            }
        }
    }
}
