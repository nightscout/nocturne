namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Generic driven port for data write-event propagation.
/// Adapters translate these into SignalR broadcasts, cache invalidation,
/// write-back to external systems, etc. Failures are non-fatal.
/// </summary>
/// <typeparam name="T">The domain model type whose lifecycle events are observed.</typeparam>
/// <remarks>
/// All methods have default no-op implementations so that sinks only need to
/// override the events they care about. The <see cref="CompositeDataEventSink{T}"/>
/// fans out calls to all registered sinks and swallows individual failures.
/// </remarks>
/// <seealso cref="CompositeDataEventSink{T}"/>
public interface IDataEventSink<in T>
{
    /// <summary>
    /// Called after one or more records of type <typeparamref name="T"/> have been created.
    /// </summary>
    /// <param name="items">The newly created records.</param>
    Task OnCreatedAsync(IReadOnlyList<T> items, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called after a single record of type <typeparamref name="T"/> has been created.
    /// </summary>
    /// <param name="item">The newly created record.</param>
    Task OnCreatedAsync(T item, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called after a record of type <typeparamref name="T"/> has been updated.
    /// </summary>
    Task OnUpdatedAsync(T item, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called before a record is deleted, allowing the sink to capture state
    /// (e.g., for cascading deletes or audit logging).
    /// </summary>
    /// <param name="id">The identifier of the record about to be deleted.</param>
    Task BeforeDeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called after a record has been deleted.
    /// </summary>
    /// <param name="item">The deleted record, or <c>null</c> if it was not available.</param>
    Task OnDeletedAsync(T? item, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Raised by the V4 glucose repositories for every committed batch, whatever its
    /// <see cref="V4.WriteOrigin"/>, with each record the batch created, updated or deleted, and only for
    /// <c>Entry</c> sinks. Deletes that bypass the repository broadcast (delete by source, delete by time
    /// range) do not raise it. A sink that must see backfills and imports overrides it.
    /// </summary>
    Task OnWrittenAsync(IReadOnlyList<T> items, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Called after a bulk delete operation has completed.
    /// </summary>
    Task OnBulkDeletedAsync(long deletedCount, CancellationToken ct = default) => Task.CompletedTask;
}
