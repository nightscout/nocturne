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

    Task<bool> AuthenticateAsync();

    Task<SyncResult> SyncDataAsync(SyncRequest request, TConfig config, CancellationToken cancellationToken, ISyncProgressReporter? progressReporter = null);

    /// <summary>
    ///     Perform a scheduled sync, resolving the range from the connector's own resume watermark
    ///     when <paramref name="since"/> is omitted.
    /// </summary>
    Task<SyncResult> SyncDataAsync(TConfig config, CancellationToken cancellationToken = default, DateTime? since = null, ISyncProgressReporter? progressReporter = null);
}