using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Entry records into v4 granular models (SensorGlucose, MeterGlucose, Calibration).
/// Handles idempotent create-or-update based on LegacyId matching.
/// </summary>
public interface IEntryDecomposer
{
    /// <summary>
    /// Decomposes a single legacy Entry into the appropriate v4 record type
    /// based on <see cref="Entry.Type"/>.
    /// </summary>
    /// <param name="entry">The legacy Entry to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing the created or updated v4 record.
    /// Returns an empty result if the entry type is unrecognized.
    /// </returns>
    Task<DecompositionResult> DecomposeAsync(Entry entry, CancellationToken ct = default);
}
