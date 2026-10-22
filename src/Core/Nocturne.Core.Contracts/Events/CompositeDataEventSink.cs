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
            await InvokeAsync(sink, s => s.OnCreatedAsync(items, ct));
    }

    /// <inheritdoc />
    public async Task OnCreatedAsync(T item, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnCreatedAsync(item, ct));
    }

    /// <inheritdoc />
    public async Task OnUpdatedAsync(T item, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnUpdatedAsync(item, ct));
    }

    /// <inheritdoc />
    public async Task BeforeDeleteAsync(string id, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.BeforeDeleteAsync(id, ct));
    }

    /// <inheritdoc />
    public async Task OnDeletedAsync(T? item, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnDeletedAsync(item, ct));
    }

    /// <inheritdoc />
    public async Task OnWrittenAsync(IReadOnlyList<T> items, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnWrittenAsync(items, ct));
    }

    /// <inheritdoc />
    public async Task OnBulkDeletedAsync(long deletedCount, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await InvokeAsync(sink, s => s.OnBulkDeletedAsync(deletedCount, ct));
    }

    /// <summary>
    /// Invokes a single sink action, catching and logging any exception so that
    /// one failing sink does not prevent others from receiving the event.
    /// </summary>
    private async Task InvokeAsync(IDataEventSink<T> sink, Func<IDataEventSink<T>, Task> action)
    {
        try
        {
            await action(sink);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Data event sink {SinkType} failed", sink.GetType().Name);
        }
    }
}
