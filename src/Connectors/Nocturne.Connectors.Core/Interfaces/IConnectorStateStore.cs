using System.Text.Json.Nodes;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     The state a connector keeps between runs, in its two slots (<see cref="ConnectorStateSlots"/>), on
///     the scope tenant's configuration row of the connector registered for <c>source</c>.
/// </summary>
/// <remarks>
///     A connector reaches it only through the base (<see cref="Services.BaseConnectorService{TConfig}"/>),
///     which owns what a cursor reset clears. A source with no registration, or a tenant with no
///     configuration of it, reads as empty and drops a write.
/// </remarks>
public interface IConnectorStateStore
{
    /// <summary>The resume state: named UTC times, empty when none is held.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> ResumeAsync(
        string source, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="changes"/> to the resume state in one write: a key with a time is
    ///     stored, a key with <c>null</c> removed, and every other key kept.
    /// </summary>
    Task SetResumeAsync(
        string source, IReadOnlyDictionary<string, DateTime?> changes, CancellationToken cancellationToken = default);

    /// <summary>The vendor state stored under <paramref name="key"/>, or <c>null</c> when none is.</summary>
    Task<JsonNode?> VendorAsync(string source, string key, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stores <paramref name="value"/> under <paramref name="key"/> in the vendor state, or removes the
    ///     key when it is <c>null</c>, leaving every other key.
    /// </summary>
    Task SetVendorAsync(string source, string key, JsonNode? value, CancellationToken cancellationToken = default);

    /// <summary>Empties the named slots.</summary>
    Task ClearAsync(string source, ConnectorStateSlots slots, CancellationToken cancellationToken = default);
}
