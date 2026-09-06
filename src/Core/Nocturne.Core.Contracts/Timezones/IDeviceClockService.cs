using Nocturne.Core.Models.Timezones;

namespace Nocturne.Core.Contracts.Timezones;

/// <summary>
/// Records device-clock offset evidence for the current tenant and derives the deviation segments a
/// connector's time mapping may correct with. Observations live in their own store — never in the
/// timezone timeline — so derived knowledge cannot clobber a manual entry and re-derivation stays
/// idempotent.
/// </summary>
/// <remarks>
/// Like <see cref="ITimezoneTimelineService"/>, implementations work in both HTTP request scopes and
/// background connector-sync scopes, resolving the tenant from the ambient tenant context.
/// </remarks>
/// <seealso cref="DeviceClockSegmenter"/>
public interface IDeviceClockService
{
    /// <summary>
    /// Evidence older than this no longer influences segmentation and is pruned; connectors gathering
    /// historical evidence need not look further back.
    /// </summary>
    const int RetentionDays = 456;

    /// <summary>
    /// Stores new observations (idempotently — re-observed evidence is not duplicated), re-derives
    /// the connector's deviation segments from all stored evidence, and returns them.
    /// </summary>
    /// <param name="connector">Connector the evidence belongs to (e.g. "glooko").</param>
    /// <param name="observations">The sync's freshly gathered evidence; may be empty.</param>
    /// <param name="expectedFallbackOffsetHours">
    /// The connector's legacy static offset (hours east of UTC), used as the expected clock where the
    /// timeline has no entry — deviations on un-seeded tenants are measured against it.
    /// </param>
    /// <param name="correctionsEnabled">
    /// Whether the tenant has enabled automatic correction for this connector. When true, a newly
    /// confirmed segment notifies the tenant owner, and a sustained change of the account's declared
    /// zone appends a timeline entry. When false the service only records and derives — evidence
    /// gathering stays on for fleet validation while nothing user-visible moves.
    /// </param>
    Task<IReadOnlyList<DeviceClockSegment>> RecordObservationsAsync(
        string connector,
        IReadOnlyList<DeviceClockObservation> observations,
        double? expectedFallbackOffsetHours,
        bool correctionsEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the stored observations, newest last; optionally filtered by connector.</summary>
    Task<IReadOnlyList<DeviceClockObservation>> GetObservationsAsync(
        string? connector = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Derives the connector's current deviation segments from stored evidence without recording
    /// anything.
    /// </summary>
    /// <param name="expectedFallbackOffsetHours">See <see cref="RecordObservationsAsync"/>.</param>
    Task<IReadOnlyList<DeviceClockSegment>> GetSegmentsAsync(
        string connector,
        double? expectedFallbackOffsetHours = null,
        CancellationToken cancellationToken = default);
}
