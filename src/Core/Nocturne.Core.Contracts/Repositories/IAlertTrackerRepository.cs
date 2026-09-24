using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="AlertTrackerState"/> and <see cref="AlertExcursion"/> persistence.
/// Consumed by <see cref="Nocturne.Core.Contracts.Alerts.IExcursionTracker"/> to track
/// excursion state across evaluations.
/// </summary>
/// <seealso cref="AlertTrackerState"/>
/// <seealso cref="AlertExcursion"/>
/// <seealso cref="AlertRule"/>
/// <seealso cref="Nocturne.Core.Contracts.Alerts.IExcursionTracker"/>
public interface IAlertTrackerRepository
{
    /// <summary>
    /// Get the tracker state for a specific alert rule.
    /// </summary>
    /// <param name="alertRuleId">The unique identifier of the <see cref="AlertRule"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="AlertTrackerState"/> for the rule, or <c>null</c> if no state has been recorded yet.</returns>
    Task<AlertTrackerState?> GetTrackerStateAsync(
        Guid alertRuleId,
        CancellationToken ct = default);

    /// <summary>
    /// Insert or update the tracker state for a rule.
    /// </summary>
    /// <param name="state">The <see cref="AlertTrackerState"/> to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertTrackerStateAsync(
        AlertTrackerState state,
        CancellationToken ct = default);

    /// <summary>
    /// Get the alert rule configuration.
    /// </summary>
    /// <param name="alertRuleId">The unique identifier of the <see cref="AlertRule"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="AlertRule"/>, or <c>null</c> if not found.</returns>
    Task<AlertRule?> GetRuleAsync(
        Guid alertRuleId,
        CancellationToken ct = default);

    /// <summary>
    /// Create a new excursion record and return it.
    /// </summary>
    /// <param name="alertRuleId">The <see cref="AlertRule"/> that triggered the excursion.</param>
    /// <param name="startedAt">UTC time when the excursion began.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created <see cref="AlertExcursion"/>.</returns>
    Task<AlertExcursion> CreateExcursionAsync(
        Guid alertRuleId,
        DateTime startedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Close an excursion by setting its <c>EndedAt</c> timestamp.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/> to close.</param>
    /// <param name="endedAt">UTC time when the excursion ended.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CloseExcursionAsync(
        Guid excursionId,
        DateTime endedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Record the start of hysteresis on an excursion.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/>.</param>
    /// <param name="hysteresisStartedAt">UTC time when hysteresis began.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetHysteresisStartedAsync(
        Guid excursionId,
        DateTime hysteresisStartedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Clear the hysteresis timestamp on an excursion (when resuming from hysteresis).
    /// </summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ClearHysteresisAsync(
        Guid excursionId,
        CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction, so the writes it makes through
    /// this repository commit or roll back together. The default, for a store with no
    /// transactions, runs it as is.
    /// </summary>
    /// <remarks>
    /// <paramref name="work"/> can run more than once when the store retries a transient failure.
    /// Each attempt starts from the store's committed rows, not from what an earlier attempt read
    /// or wrote. A failure can leave it unknown whether the commit landed: the store then asks
    /// <paramref name="verifySucceeded"/>, with the result that attempt produced, whether its
    /// writes are there, and returns that result instead of running <paramref name="work"/>
    /// again. Without it, a commit that landed but reported failure runs the work twice.
    /// </remarks>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken ct = default) => work(ct);
}
