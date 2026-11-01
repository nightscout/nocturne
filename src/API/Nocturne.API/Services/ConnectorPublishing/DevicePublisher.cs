using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Logging;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes device status and device event data received from connectors into
/// the Nocturne domain via <see cref="IDeviceStatusService"/> and <see cref="IDeviceEventRepository"/>.
/// </summary>
/// <seealso cref="IDevicePublisher"/>
internal sealed class DevicePublisher : ConnectorPublisherBase, IDevicePublisher
{
    private readonly IDeviceStatusService _deviceStatuses;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly IPatientDeviceRepository _patientDeviceRepository;
    private readonly IPumpSnapshotRepository _pumpSnapshotRepository;
    private readonly IUploaderSnapshotRepository _uploaderSnapshotRepository;

    public DevicePublisher(
        IDeviceStatusService deviceStatuses,
        IDeviceEventRepository deviceEventRepository,
        IPatientDeviceStamper patientDeviceStamper,
        IAuditContext auditContext,
        IApsSnapshotRepository apsSnapshotRepository,
        IPatientDeviceRepository patientDeviceRepository,
        IPumpSnapshotRepository pumpSnapshotRepository,
        IUploaderSnapshotRepository uploaderSnapshotRepository,
        PublishSkipTally skips,
        ITenantAccessor tenantAccessor,
        ILogger<DevicePublisher> logger)
        : base(auditContext, skips, tenantAccessor, logger)
    {
        _deviceStatuses = deviceStatuses ?? throw new ArgumentNullException(nameof(deviceStatuses));
        _deviceEventRepository = deviceEventRepository ?? throw new ArgumentNullException(nameof(deviceEventRepository));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
        _apsSnapshotRepository = apsSnapshotRepository ?? throw new ArgumentNullException(nameof(apsSnapshotRepository));
        _patientDeviceRepository = patientDeviceRepository ?? throw new ArgumentNullException(nameof(patientDeviceRepository));
        _pumpSnapshotRepository = pumpSnapshotRepository ?? throw new ArgumentNullException(nameof(pumpSnapshotRepository));
        _uploaderSnapshotRepository = uploaderSnapshotRepository ?? throw new ArgumentNullException(nameof(uploaderSnapshotRepository));
    }

    public async Task<bool> PublishPatientDevicesAsync(
        IEnumerable<PatientDevice> devices,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = devices.ToList();
            if (list.Count == 0) return true;

            using (PushSystemAudit())
            {
                foreach (var device in list)
                {
                    // Upsert on the connector's deterministic Id so re-syncs update the same row.
                    var existing = await _patientDeviceRepository.GetByIdAsync(device.Id, cancellationToken);
                    if (existing != null)
                        await _patientDeviceRepository.UpdateAsync(device.Id, device, origin, cancellationToken);
                    else
                        await _patientDeviceRepository.CreateAsync(device, origin, cancellationToken);
                }
            }

            Logger.LogDebug("Published {Count} PatientDevice records for {Source}", list.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to publish PatientDevice records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var skippedDeleted = (await _deviceStatuses.CreateDeviceStatusesAsync(
                [.. deviceStatuses], source, origin, cancellationToken)).SkippedDeleted;

            Logger.LogSkippedDeleted(nameof(DeviceStatus), skippedDeleted);
            RecordSkippedDeleted(skippedDeleted);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to publish device status for {Source}", source);
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>A device status decomposes into APS, pump and uploader snapshots. One that yields none
    /// is never held, so it is skipped rather than re-decomposed on every sync.</remarks>
    public Task<int?> PublishRecentDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishUnheldAsync(
            deviceStatuses.Where(_deviceStatuses.HasLegacyKeyedSnapshot), d => d.Id,
            unheld => PublishDeviceStatusAsync(unheld, source, origin, cancellationToken),
            source,
            ids => _apsSnapshotRepository.GetHeldLegacyIdsAsync(ids, cancellationToken),
            ids => _pumpSnapshotRepository.GetHeldLegacyIdsAsync(ids, cancellationToken),
            ids => _uploaderSnapshotRepository.GetHeldLegacyIdsAsync(ids, cancellationToken));

    public Task<bool> PublishDeviceEventsAsync(
        IEnumerable<DeviceEvent> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(
            records, _deviceEventRepository, source, origin, cancellationToken,
            beforeWrite: recordList => _patientDeviceStamper.StampDeviceEventsAsync(
                recordList, source, cancellationToken));

    /// <inheritdoc cref="ConnectorPublisherBase.LatestTimestampAsync" />
    /// <remarks>A device-status sync stores APS, pump, and uploader snapshots.</remarks>
    public Task<DateTime?> GetLatestDeviceStatusTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
        => LatestTimestampAsync(
            () => _apsSnapshotRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _pumpSnapshotRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _uploaderSnapshotRepository.GetLatestTimestampAsync(source, cancellationToken));
}
