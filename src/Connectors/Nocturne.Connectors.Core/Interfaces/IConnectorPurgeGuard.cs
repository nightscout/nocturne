namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Stops a connector's syncs while its data is purged, for a connector whose syncs the
///     persisted active flag does not govern.
/// </summary>
public interface IConnectorPurgeGuard
{
    /// <summary>The connector name the guard covers, matched without regard to case.</summary>
    string ConnectorName { get; }

    /// <summary>
    ///     Waits briefly for any running sync of the current tenant to finish, then holds off every
    ///     sync until the returned lease is disposed. Returns null when a sync is still running.
    /// </summary>
    Task<IAsyncDisposable?> PauseSyncsAsync(CancellationToken ct);
}
