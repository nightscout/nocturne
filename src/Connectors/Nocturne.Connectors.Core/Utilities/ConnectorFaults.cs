using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    public static bool IsSourceFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or TimeoutException or IOException or InvalidDataException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        || IsMalformedRecord(ex);

    /// <summary>
    ///     The elements of a vendor array a mapper can map. A <c>null</c> element is a malformed record
    ///     too, but dereferencing it raises <see cref="NullReferenceException"/>, which
    ///     <see cref="IsMalformedRecord"/> leaves to fail the run because the same exception also marks a
    ///     connector defect. Mappers drop null elements here, before the loop, instead.
    /// </summary>
    /// <returns>The non-null elements in order; empty when <paramref name="records"/> is <c>null</c>.</returns>
    public static IReadOnlyList<T> NonNullRecords<T>(
        IEnumerable<T?>? records,
        ILogger logger,
        string connectorSource,
        string recordType)
        where T : class
    {
        if (records == null)
            return [];

        var kept = new List<T>();
        var dropped = 0;
        foreach (var record in records)
        {
            if (record == null)
                dropped++;
            else
                kept.Add(record);
        }

        if (dropped > 0)
            logger.LogInformation(
                "[{ConnectorSource}] Dropped {Count} null {RecordType} records",
                connectorSource, dropped, recordType);

        return kept;
    }
}
