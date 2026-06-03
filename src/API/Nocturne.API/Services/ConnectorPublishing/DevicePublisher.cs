using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes device status and device event data received from connectors into
/// the Nocturne domain via <see cref="IDeviceStatusDecomposer"/> and <see cref="IDeviceEventRepository"/>.
/// </summary>
/// <seealso cref="IDevicePublisher"/>
internal sealed class DevicePublisher : IDevicePublisher
{
    private readonly IDeviceStatusDecomposer _decomposer;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly ILogger<DevicePublisher> _logger;

    public DevicePublisher(
        IDeviceStatusDecomposer decomposer,
        IDeviceEventRepository deviceEventRepository,
        IApsSnapshotRepository apsSnapshotRepository,
        ILogger<DevicePublisher> logger)
    {
        _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
        _deviceEventRepository = deviceEventRepository ?? throw new ArgumentNullException(nameof(deviceEventRepository));
        _apsSnapshotRepository = apsSnapshotRepository ?? throw new ArgumentNullException(nameof(apsSnapshotRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var ds in deviceStatuses)
            {
                await _decomposer.DecomposeAsync(ds, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish device status for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishDeviceEventsAsync(
        IEnumerable<DeviceEvent> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _deviceEventRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} DeviceEvent records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish DeviceEvent records for {Source}", source);
            return false;
        }
    }

    /// <summary>
    /// Returns the timestamp of the most recent device-status record for the current tenant,
    /// using the <c>aps_snapshots</c> watermark — the dominant loop/openaps/pump device-status
    /// source after decomposition. Like <see cref="ITreatmentPublisher.GetLatestTreatmentTimestampAsync"/>,
    /// this is not source-filtered and returns the global latest for the tenant.
    /// </summary>
    public async Task<DateTime?> GetLatestDeviceStatusTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
        => await _apsSnapshotRepository.GetLatestTimestampAsync(null, cancellationToken);
}
