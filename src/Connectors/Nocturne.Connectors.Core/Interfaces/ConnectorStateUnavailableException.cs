namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     The store behind <see cref="IConnectorStateStore"/> could not be read or written: its database
///     failed. The base connector service
///     (<see cref="Services.BaseConnectorService{TConfig}"/>) lets a best-effort read or write go on
///     without the state when it sees this, and lets any other exception fail the run.
/// </summary>
/// <remarks>The failure the store raised is the inner exception.</remarks>
public sealed class ConnectorStateUnavailableException(string message, Exception inner)
    : Exception(message, inner);
