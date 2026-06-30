using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Audit;
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
    private readonly IAuditContext _auditContext;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly IPumpSnapshotRepository _pumpSnapshotRepository;
    private readonly IUploaderSnapshotRepository _uploaderSnapshotRepository;
    private readonly ILogger<DevicePublisher> _logger;

    public DevicePublisher(
        IDeviceStatusDecomposer decomposer,
        IDeviceEventRepository deviceEventRepository,
        IAuditContext auditContext,
        IApsSnapshotRepository apsSnapshotRepository,
        IPumpSnapshotRepository pumpSnapshotRepository,
        IUploaderSnapshotRepository uploaderSnapshotRepository,
        ILogger<DevicePublisher> logger)
    {
        _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
        _deviceEventRepository = deviceEventRepository ?? throw new ArgumentNullException(nameof(deviceEventRepository));
        _auditContext = auditContext;
        _apsSnapshotRepository = apsSnapshotRepository ?? throw new ArgumentNullException(nameof(apsSnapshotRepository));
        _pumpSnapshotRepository = pumpSnapshotRepository ?? throw new ArgumentNullException(nameof(pumpSnapshotRepository));
        _uploaderSnapshotRepository = uploaderSnapshotRepository ?? throw new ArgumentNullException(nameof(uploaderSnapshotRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var ds in deviceStatuses)
            {
                await _decomposer.DecomposeAsync(ds, source, origin, cancellationToken);
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
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            using (SystemAuditScope.Push(_auditContext))
                await _deviceEventRepository.BulkCreateAsync(recordList, origin, cancellationToken);
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
    /// Returns the resume watermark for the connector device-status sync: the latest stored snapshot
    /// timestamp (across APS, pump, and uploader snapshots) for THIS source. Source-scoping is
    /// required for multi-connector catch-up — a tenant-global latest mis-classifies a newly enabled
    /// connector's first device-status sync as incremental and skips its backfill. Mirrors
    /// <see cref="ITreatmentPublisher.GetLatestTreatmentTimestampAsync"/>.
    /// </summary>
    public async Task<DateTime?> GetLatestDeviceStatusTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var candidates = new[]
        {
            await _apsSnapshotRepository.GetLatestTimestampAsync(source, cancellationToken),
            await _pumpSnapshotRepository.GetLatestTimestampAsync(source, cancellationToken),
            await _uploaderSnapshotRepository.GetLatestTimestampAsync(source, cancellationToken),
        };

        var present = candidates.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        return present.Count > 0 ? present.Max() : null;
    }
}
