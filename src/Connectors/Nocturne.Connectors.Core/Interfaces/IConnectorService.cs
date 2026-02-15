using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Interface for data source connector services that fetch glucose data from various platforms
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public interface IConnectorService<in TConfig> : IDisposable
    where TConfig : IConnectorConfiguration
{
    /// <summary>
    ///     Get the name of this connector service
    /// </summary>
    string ServiceName { get; }

    /// <summary>
    ///     Gets the list of data types supported by this connector
    /// </summary>
    List<SyncDataType> SupportedDataTypes { get; }

    /// <summary>
    ///     Authenticate with the data source
    /// </summary>
    Task<bool> AuthenticateAsync();

    /// <summary>
    ///     Fetch glucose entries from the data source
    /// </summary>
    /// <param name="since">Fetch entries since this timestamp (optional)</param>
    Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null);

    /// <summary>
    ///     Perform a granular sync operation
    /// </summary>
    Task<SyncResult> SyncDataAsync(SyncRequest request, TConfig config, CancellationToken cancellationToken);
}