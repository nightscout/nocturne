using System.Text.Json.Nodes;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Tests.Shared.Connectors;

/// <summary>
///     One connector's state between runs, as the store keeps it: <see cref="Resume"/> holds the
///     named UTC times and <see cref="Vendor"/> the JSON the connector stored. Every source reads and
///     writes the same two maps, so give each connector under test its own.
/// </summary>
public sealed class InMemoryConnectorStateStore : IConnectorStateStore
{
    private readonly object _gate = new();

    public Dictionary<string, DateTime> Resume { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonNode> Vendor { get; } = new(StringComparer.Ordinal);

    /// <summary>Observes each key as it is written or removed.</summary>
    public Action<string>? OnWrite { get; init; }

    /// <summary>Runs before each read, to fault it.</summary>
    public Action<CancellationToken>? BeforeRead { get; init; }

    /// <summary>Runs before each write, to fault it.</summary>
    public Action<CancellationToken>? BeforeWrite { get; init; }

    public Task<IReadOnlyDictionary<string, DateTime>> ResumeAsync(
        string source, CancellationToken cancellationToken = default)
    {
        BeforeRead?.Invoke(cancellationToken);
        lock (_gate)
            return Task.FromResult<IReadOnlyDictionary<string, DateTime>>(new Dictionary<string, DateTime>(Resume));
    }

    public Task SetResumeAsync(
        string source, IReadOnlyDictionary<string, DateTime?> changes, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke(cancellationToken);
        foreach (var (key, at) in changes)
        {
            lock (_gate)
            {
                if (at is { } time)
                    Resume[key] = time;
                else
                    Resume.Remove(key);
            }

            OnWrite?.Invoke(key);
        }

        return Task.CompletedTask;
    }

    public Task<JsonNode?> VendorAsync(string source, string key, CancellationToken cancellationToken = default)
    {
        BeforeRead?.Invoke(cancellationToken);
        lock (_gate)
            return Task.FromResult(Vendor.TryGetValue(key, out var value) ? value.DeepClone() : null);
    }

    public Task SetVendorAsync(string source, string key, JsonNode? value, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke(cancellationToken);
        lock (_gate)
        {
            if (value is null)
                Vendor.Remove(key);
            else
                Vendor[key] = value.DeepClone();
        }

        OnWrite?.Invoke(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string source, ConnectorStateSlots slots, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke(cancellationToken);
        lock (_gate)
        {
            if (slots.HasFlag(ConnectorStateSlots.Resume))
                Resume.Clear();
            if (slots.HasFlag(ConnectorStateSlots.Vendor))
                Vendor.Clear();
        }

        return Task.CompletedTask;
    }
}
