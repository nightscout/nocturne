using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Unified orchestration layer for decomposing legacy records into v4 models.
/// Dispatches to the appropriate <see cref="IDecomposer{T}"/> and absorbs errors internally.
/// </summary>
/// <seealso cref="IDecomposer{T}"/>
/// <seealso cref="DecompositionResult"/>
/// <seealso cref="BatchDecompositionResult"/>
public interface IDecompositionPipeline
{
    /// <summary>
    /// Decomposes a batch of legacy records into v4 models, dispatching each to the
    /// appropriate <see cref="IDecomposer{T}"/>. Individual failures are captured in the
    /// result rather than thrown.
    /// </summary>
    /// <typeparam name="T">The legacy domain model type.</typeparam>
    /// <param name="records">The legacy records to decompose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="BatchDecompositionResult"/> summarizing successes and failures.</returns>
    Task<BatchDecompositionResult> DecomposeAsync<T>(IEnumerable<T> records, WriteOrigin origin, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Decomposes a single legacy record into v4 models via the appropriate
    /// <see cref="IDecomposer{T}"/>. Errors are captured rather than thrown.
    /// </summary>
    /// <typeparam name="T">The legacy domain model type.</typeparam>
    /// <param name="record">The legacy record to decompose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="BatchDecompositionResult"/> with a single entry.</returns>
    Task<BatchDecompositionResult> DecomposeAsync<T>(T record, WriteOrigin origin, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Deletes all v4 records that were decomposed from a legacy record of type
    /// <typeparamref name="T"/> with the given identifier.
    /// </summary>
    /// <typeparam name="T">The legacy domain model type.</typeparam>
    /// <param name="legacyId">The legacy record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of v4 records deleted.</returns>
    Task<int> DeleteByLegacyIdAsync<T>(string legacyId, WriteOrigin origin, CancellationToken ct = default) where T : class;
}
