namespace Nocturne.Widget.Contracts;

/// <summary>
/// Writes the raw V4 summary JSON to the local file the taskbar mod reads
/// (<c>%LOCALAPPDATA%\Nocturne\glucose.json</c>), so the widget acts as a second data source
/// alongside the desktop companion.
/// </summary>
public interface IGlucoseFilePublisher
{
    /// <summary>
    /// Atomically writes <paramref name="rawSummaryJson"/> to the glucose file. No-op for null or
    /// empty input. Never throws — failures are logged and swallowed.
    /// </summary>
    Task PublishAsync(string rawSummaryJson);
}
