using Nocturne.Core.Models;
using Nocturne.Core.Models.Services;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Service for managing and querying data sources connected to Nocturne
/// </summary>
public interface IDataSourceService
{
    /// <summary>
    /// Get all data sources that have sent data to this Nocturne instance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active data sources with their status</returns>
    Task<List<DataSourceInfo>> GetActiveDataSourcesAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get detailed information about a specific data source
    /// </summary>
    /// <param name="deviceId">The device identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Data source info if found</returns>
    Task<DataSourceInfo?> GetDataSourceInfoAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get available connectors that can be configured
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available connectors</returns>
    Task<List<AvailableConnector>> GetAvailableConnectorsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get capabilities for a specific connector.
    /// </summary>
    /// <param name="connectorId">The connector ID (e.g., "dexcom", "glooko")</param>
    /// <returns>Connector capabilities if found</returns>
    ConnectorCapabilities? GetConnectorCapabilities(string connectorId);

    /// <summary>
    /// Get uploader apps that can push data to Nocturne
    /// </summary>
    /// <returns>List of uploader apps with setup instructions</returns>
    List<UploaderApp> GetUploaderApps();

    /// <summary>
    /// Get the complete services overview
    /// </summary>
    /// <param name="baseUrl">Base URL for API endpoint info</param>
    /// <param name="isAuthenticated">Whether the current request is authenticated</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete services overview</returns>
    Task<ServicesOverview> GetServicesOverviewAsync(
        string baseUrl,
        bool isAuthenticated,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete all data from a specific data source.
    /// This is a destructive operation that cannot be undone.
    /// </summary>
    /// <param name="dataSourceId">The data source ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing number of entries and treatments deleted</returns>
    Task<DataSourceDeleteResult> DeleteDataSourceDataAsync(
        string dataSourceId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete all demo data (data from the demo-service source).
    /// This is safe to call as demo data can be easily regenerated.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing number of entries and treatments deleted</returns>
    Task<DataSourceDeleteResult> DeleteDemoDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all data from a specific connector.
    /// This is a destructive operation that cannot be undone.
    /// </summary>
    /// <param name="connectorId">The connector ID (e.g., "dexcom", "glooko")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the delete operation</returns>
    Task<DataSourceDeleteResult> DeleteConnectorDataAsync(
        string connectorId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a summary of data counts for a specific connector.
    /// </summary>
    /// <param name="connectorId">The connector ID (e.g., "dexcom", "glooko")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Summary of data counts by type</returns>
    Task<ConnectorDataSummary> GetConnectorDataSummaryAsync(
        string connectorId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get statistics for a data source aggregated across all tables (V4 glucose repos,
    /// treatments, device status, state spans, etc.).
    /// </summary>
    /// <param name="dataSource">The data source identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated statistics for the data source.</returns>
    Task<DataSourceStats> GetDataSourceStatsAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the latest entry timestamp across all glucose tables (sensor glucose, meter glucose, calibrations)
    /// for a given data source.
    /// </summary>
    /// <param name="dataSource">The data source identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest timestamp, or null if no records exist.</returns>
    Task<DateTime?> GetLatestGlucoseTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the oldest entry timestamp across all glucose tables (sensor glucose, meter glucose, calibrations)
    /// for a given data source.
    /// </summary>
    /// <param name="dataSource">The data source identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The oldest timestamp, or null if no records exist.</returns>
    Task<DateTime?> GetOldestGlucoseTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the latest treatment timestamp across all V4 treatment tables for a given data source.
    /// </summary>
    Task<DateTime?> GetLatestTreatmentTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the oldest treatment timestamp across all V4 treatment tables for a given data source.
    /// </summary>
    Task<DateTime?> GetOldestTreatmentTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete all glucose data (sensor glucose, meter glucose, calibrations) for a given data source.
    /// </summary>
    /// <param name="dataSource">The data source identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total number of records deleted across all glucose tables.</returns>
    Task<long> DeleteGlucoseDataBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );
}
