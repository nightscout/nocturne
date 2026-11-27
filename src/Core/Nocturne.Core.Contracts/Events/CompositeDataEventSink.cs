using Microsoft.Extensions.Logging;

namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Fans out every <see cref="IDataEventSink{T}"/> call to all registered sinks.
/// Each sink is invoked independently so that one failure cannot block others.
/// </summary>
public class CompositeDataEventSink<T>(
    IEnumerable<IDataEventSink<T>> sinks,
    ILogger<CompositeDataEventSink<T>>? logger = null) : IDataEventSink<T>
{
    private readonly IReadOnlyList<IDataEventSink<T>> _sinks = sinks.ToList();

    /// <inheritdoc />
    public async Task OnCreatedAsync(IReadOnlyList<T> items, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnCreatedAsync(items, ct), ct);
    }

    /// <inheritdoc />
    public async Task OnCreatedAsync(T item, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnCreatedAsync(item, ct), ct);
    }

    /// <inheritdoc />
    public async Task OnUpdatedAsync(T item, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnUpdatedAsync(item, ct), ct);
    }

    /// <inheritdoc />
    public async Task OnDeletedAsync(T? item, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnDeletedAsync(item, ct), ct);
    }

    /// <inheritdoc />
    public async Task OnWrittenAsync(IReadOnlyList<T> items, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnWrittenAsync(items, ct), ct);
    }

    /// <inheritdoc />
    public async Task OnBulkDeletedAsync(long deletedCount, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnBulkDeletedAsync(deletedCount, ct), ct);
    }

    /// <summary>
    /// Invokes a single sink action, catching and logging any exception so that
    /// one failing sink does not prevent others from receiving the event. The sinks (SignalR
    /// fan-out, cache invalidation, Nightscout write-back) fail in unrelated ways, so there is
    /// no narrower set of exceptions to isolate. The caller's own cancellation is not a sink failure
    /// and reaches the caller.
    /// </summary>
    private async Task InvokeAsync(
        IDataEventSink<T> sink, Func<IDataEventSink<T>, Task> action, CancellationToken ct)
    {
        try
        {
            await action(sink);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogError(ex, "Data event sink {SinkType} failed", sink.GetType().Name);
        }
    }
}
