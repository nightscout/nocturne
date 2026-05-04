using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Projects V4 granular records (SensorGlucose, Bolus, CarbIntake, BGCheck, Note, DeviceEvent)
/// back into the legacy Entry and Treatment shapes required by the v1/v2/v3 API endpoints.
/// </summary>
/// <seealso cref="IEntryService"/>
/// <seealso cref="ITreatmentService"/>
public interface IV4ToLegacyProjectionService
{
    /// <summary>
    /// Returns a set of legacy <see cref="Entry"/> objects synthesised from V4 SensorGlucose records.
    /// These supplement the entries already stored in the legacy entries table for connectors
    /// that write V4 directly.
    /// </summary>
    Task<IEnumerable<Entry>> GetProjectedEntriesAsync(
        long? fromMills,
        long? toMills,
        int limit,
        int offset,
        bool descending,
        CancellationToken ct = default
    );

    /// <summary>
    /// Returns the single most-recent legacy <see cref="Entry"/> synthesised from V4 SensorGlucose records,
    /// or <c>null</c> if no V4 glucose records exist.
    /// </summary>
    Task<Entry?> GetLatestProjectedEntryAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a set of legacy <see cref="Treatment"/> objects synthesised from V4 treatment records
    /// (Bolus, CarbIntake, BGCheck, Note, DeviceEvent).
    /// These supplement the treatments already stored in the legacy treatments table for connectors
    /// that write V4 directly.
    /// </summary>
    Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        int limit,
        bool nativeOnly = true,
        CancellationToken ct = default
    );

    /// <summary>
    /// Returns legacy <see cref="Treatment"/> objects synthesised from V4 records
    /// whose <c>ModifiedAt</c> is at or after the given threshold. Used for v3 incremental sync.
    /// </summary>
    Task<IEnumerable<Treatment>> GetProjectedTreatmentsModifiedSinceAsync(
        long lastModifiedMills,
        int limit,
        CancellationToken ct = default
    );
}
