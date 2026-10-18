using System.Text.Json;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     The failures a connector survives by skipping what raised them. Anything outside these is a
///     defect in the connector rather than in what the source sent, and is left to fail the run so it
///     is seen.
/// </summary>
public static class ConnectorFaults
{
    /// <summary>
    ///     One malformed vendor record: a timestamp or number that will not parse, a value outside the
    ///     range a conversion accepts, a JSON element of the wrong kind, or a missing key. A mapper
    ///     catches these to skip that record and keep the rest of the batch.
    /// </summary>
    public static bool IsMalformedRecord(Exception ex) =>
        ex is FormatException
            or ArgumentException
            or OverflowException
            or InvalidCastException
            or InvalidOperationException
            or KeyNotFoundException
            or JsonException;

    /// <summary>
    ///     A source that did not answer usably: unreachable, refused, timed out, or answered with a
    ///     payload that will not read. A fetch catches these to report its types as not fetched and let
    ///     the rest of the run continue. A cancellation the caller asked for through
    ///     <paramref name="cancellationToken"/> is not one; a timeout surfaces as the same exception type
    ///     without it.
    /// </summary>
    public static bool IsSourceFailure(Exception ex, CancellationToken cancellationToken = default) =>
        ex is HttpRequestException or TimeoutException or IOException or InvalidDataException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        || IsMalformedRecord(ex);
}
