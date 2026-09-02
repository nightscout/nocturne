using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
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
internal sealed class DevicePublisher : ConnectorPublisherBase, IDevicePublisher
{
    private readonly IDeviceStatusDecomposer _decomposer;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly IPumpSnapshotRepository _pumpSnapshotRepository;
    private readonly IUploaderSnapshotRepository _uploaderSnapshotRepository;

    public DevicePublisher(
        IDeviceStatusDecomposer decomposer,
        IDeviceEventRepository deviceEventRepository,
        IPatientDeviceStamper patientDeviceStamper,
        IAuditContext auditContext,
        IApsSnapshotRepository apsSnapshotRepository,
        IPumpSnapshotRepository pumpSnapshotRepository,
        IUploaderSnapshotRepository uploaderSnapshotRepository,
        ILogger<DevicePublisher> logger)
        : base(auditContext, logger)
    {
        _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
        _deviceEventRepository = deviceEventRepository ?? throw new ArgumentNullException(nameof(deviceEventRepository));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
        _apsSnapshotRepository = apsSnapshotRepository ?? throw new ArgumentNullException(nameof(apsSnapshotRepository));
        _pumpSnapshotRepository = pumpSnapshotRepository ?? throw new ArgumentNullException(nameof(pumpSnapshotRepository));
        _uploaderSnapshotRepository = uploaderSnapshotRepository ?? throw new ArgumentNullException(nameof(uploaderSnapshotRepository));
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
            Logger.LogError(ex, "Failed to publish device status for {Source}", source);
            return false;
        }
    }

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
