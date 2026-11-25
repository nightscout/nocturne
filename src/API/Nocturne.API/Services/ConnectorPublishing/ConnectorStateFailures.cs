using System.Text.Json.Nodes;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
///     The <see cref="IConnectorStateStore"/> a connector holds: the scope's store, with a failure of
///     the database (<see cref="StoreFailure"/>) raised as <see cref="ConnectorStateUnavailableException"/>,
///     the one store failure the connector layer can name without referencing EF. Every other exception
///     passes through unchanged. The API's own readers take the undecorated store and see the raw
///     failure.
/// </summary>
public sealed class ConnectorStateFailures(IConnectorStateStore inner) : IConnectorStateStore
{
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, DateTime>> ResumeAsync(
        string source, CancellationToken cancellationToken = default) =>
        Translated(() => inner.ResumeAsync(source, cancellationToken));

    /// <inheritdoc />
    public Task SetResumeAsync(
        string source, IReadOnlyDictionary<string, DateTime?> changes, CancellationToken cancellationToken = default) =>
        Translated(() => inner.SetResumeAsync(source, changes, cancellationToken));

    /// <inheritdoc />
    public Task<JsonNode?> VendorAsync(string source, string key, CancellationToken cancellationToken = default) =>
        Translated(() => inner.VendorAsync(source, key, cancellationToken));

    /// <inheritdoc />
    public Task SetVendorAsync(
        string source, string key, JsonNode? value, CancellationToken cancellationToken = default) =>
        Translated(() => inner.SetVendorAsync(source, key, value, cancellationToken));

    /// <inheritdoc />
    public Task ClearAsync(string source, ConnectorStateSlots slots, CancellationToken cancellationToken = default) =>
        Translated(() => inner.ClearAsync(source, slots, cancellationToken));

    private static Task Translated(Func<Task> call) =>
        Translated(async () =>
        {
            await call();
            return true;
        });

    private static async Task<T> Translated<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            throw new ConnectorStateUnavailableException(ex.Message, ex);
        }
    }
}
