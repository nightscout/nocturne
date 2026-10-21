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
    /// <param name="alertRuleId">The unique identifier of the <see cref="AlertRule"/>.</param>
    /// <returns>The <see cref="AlertTrackerState"/> for the rule, or <c>null</c> if no state has been recorded yet.</returns>
    Task<AlertTrackerState?> GetTrackerStateAsync(
        Guid alertRuleId,
        CancellationToken ct = default);

    /// <summary>
    /// Insert or update the tracker state for a rule.
    /// </summary>
    /// <param name="state">The <see cref="AlertTrackerState"/> to persist.</param>
    Task UpsertTrackerStateAsync(
        AlertTrackerState state,
        CancellationToken ct = default);

    /// <param name="alertRuleId">The unique identifier of the <see cref="AlertRule"/>.</param>
    /// <returns>The <see cref="AlertRule"/>, or <c>null</c> if not found.</returns>
    Task<AlertRule?> GetRuleAsync(
        Guid alertRuleId,
        CancellationToken ct = default);

    /// <param name="alertRuleId">The <see cref="AlertRule"/> that triggered the excursion.</param>
    /// <param name="startedAt">UTC time when the excursion began.</param>
    /// <returns>The newly created <see cref="AlertExcursion"/>.</returns>
    Task<AlertExcursion> CreateExcursionAsync(
        Guid alertRuleId,
        DateTime startedAt,
        CancellationToken ct = default);

    /// <summary>The excursion with <paramref name="excursionId"/>, or <c>null</c> if there is none.</summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/>.</param>
    Task<AlertExcursion?> GetExcursionAsync(
        Guid excursionId,
        CancellationToken ct = default);

    /// <summary>
    /// Close an excursion by setting its <c>EndedAt</c> timestamp.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/> to close.</param>
    /// <param name="endedAt">UTC time when the excursion ended.</param>
    Task CloseExcursionAsync(
        Guid excursionId,
        DateTime endedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Record the start of hysteresis on an excursion.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/>.</param>
    /// <param name="hysteresisStartedAt">UTC time when hysteresis began.</param>
    Task SetHysteresisStartedAsync(
        Guid excursionId,
        DateTime hysteresisStartedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Clear the hysteresis timestamp on an excursion (when resuming from hysteresis).
    /// </summary>
    /// <param name="excursionId">The unique identifier of the <see cref="AlertExcursion"/>.</param>
    Task ClearHysteresisAsync(
        Guid excursionId,
        CancellationToken ct = default);

    /// <summary>
    /// Holds <paramref name="alertRuleId"/>'s transition lock until the transaction
    /// (<see cref="ExecuteInTransactionAsync{T}"/>) it is taken in ends. Every writer of the rule's
    /// tracker state already holds the API's in-process per-rule lease
    /// (<c>AlertRuleEvaluationGate</c>) across read, decide and write, so no writer in the same
    /// process changes the state under another. This lock is a backstop for a second API process
    /// briefly overlapping the first, such as a pod being drained while its replacement starts. Call
    /// it on the repository the transaction hands its work. The default, for a store no other
    /// process shares, takes nothing.
    /// </summary>
    /// <param name="alertRuleId">The rule whose transition is about to be written.</param>
    Task LockRuleAsync(Guid alertRuleId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction, so the writes it makes through
    /// the repository it is handed commit or roll back together. That repository runs on the
    /// transaction; calls on this one outside it each run on their own. The default, for a store
    /// with no transactions, hands over this repository and runs the work as is.
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
        Func<IAlertTrackerRepository, CancellationToken, Task<T>> work,
        Func<IAlertTrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken ct = default) => work(this, ct);
}
