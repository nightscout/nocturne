using Nocturne.Core.Contracts.Repositories;

namespace Nocturne.Infrastructure.Data.Abstractions;

/// <summary>
/// The tenant's consumable lock, which writers that create a tracker for a piece of hardware take in
/// turn, so each finds what the one before it created.
/// </summary>
public interface ITrackerConsumableLock
{
    /// <summary>
    /// Runs <paramref name="work"/> in one transaction holding the lock. <paramref name="work"/> is
    /// handed a tracker repository and the unit of work of that transaction, for writers that must
    /// commit with it.
    /// </summary>
    /// <remarks>
    /// <paramref name="work"/> can run more than once when the store retries a transient failure.
    /// <paramref name="verifySucceeded"/> judges, after a commit that reported failure, whether the
    /// result the attempt returned landed anyway.
    /// </remarks>
    Task<T> ExecuteAsync<T>(
        Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<T>> work,
        Func<ITrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken cancellationToken = default);
}
