using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Interface for data source connector services that fetch glucose data from various platforms
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public interface IConnectorService<in TConfig> : IDisposable
    where TConfig : IConnectorConfiguration
{
    string ServiceName { get; }

    List<SyncDataType> SupportedDataTypes { get; }

    Task<SyncResult> SyncDataAsync(SyncRequest request, TConfig config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null);

    /// <summary>
    ///     Perform a scheduled sync, each data family resuming from its own point, or from
    ///     <paramref name="since"/> as a caller's lower bound when it is given.
    /// </summary>
    Task<SyncResult> SyncDataAsync(TConfig config, CancellationToken cancellationToken = default, DateTime? since = null, ISyncProgressReporter? progressReporter = null);
}