namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     A store a connector holds could not be read or written: its database failed. The connector
///     holds the stores of its publisher (<see cref="IConnectorPublisher"/>) and, for Google Health,
///     its reading writer, each raised this way by the API. The state best-effort helpers of the base
///     connector service (<see cref="Services.BaseConnectorService{TConfig}"/>) and the Google Health
///     reconciliation cleanup go on without the store when they see this; the rest of a run lets it,
///     and any other exception, fail the run.
/// </summary>
/// <remarks>The failure the store raised is the inner exception.</remarks>
public sealed class ConnectorStoreUnavailableException(string message, Exception inner)
    : Exception(message, inner);
