using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes CGM glucose readings from connectors into the Nocturne domain, writing to both the
/// legacy <see cref="IEntryService"/> and the v4 <see cref="ISensorGlucoseRepository"/>, and
/// triggering alert evaluation via <see cref="IAlertOrchestrator"/> after each successful write.
/// </summary>
/// <seealso cref="IGlucosePublisher"/>
internal sealed class GlucosePublisher : IGlucosePublisher
{
    private readonly IEntryService _entryService;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IMeterGlucoseRepository _meterGlucoseRepository;
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly ICanonicalAlertEvaluator _alertEvaluator;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<GlucosePublisher> _logger;

    public GlucosePublisher(
        IEntryService entryService,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IMeterGlucoseRepository meterGlucoseRepository,
        IPatientDeviceStamper patientDeviceStamper,
        ICanonicalAlertEvaluator alertEvaluator,
        IAuditContext auditContext,
        ILogger<GlucosePublisher> logger)
    {
        _entryService = entryService ?? throw new ArgumentNullException(nameof(entryService));
        _sensorGlucoseRepository = sensorGlucoseRepository ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _meterGlucoseRepository = meterGlucoseRepository ?? throw new ArgumentNullException(nameof(meterGlucoseRepository));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
        _alertEvaluator = alertEvaluator ?? throw new ArgumentNullException(nameof(alertEvaluator));
        _auditContext = auditContext;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PublishEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var entryList = entries.ToList();
            await _entryService.CreateEntriesAsync(entryList, origin, cancellationToken);
            await _alertEvaluator.EvaluateAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish entries for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishSensorGlucoseAsync(
        IEnumerable<SensorGlucose> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _patientDeviceStamper.StampAsync(recordList, [DeviceCategory.CGM], source, cancellationToken);
            using (SystemAuditScope.Push(_auditContext))
                await _sensorGlucoseRepository.BulkCreateAsync(recordList, origin, cancellationToken);
            await _alertEvaluator.EvaluateAsync(cancellationToken);

            _logger.LogDebug("Published {Count} SensorGlucose records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish SensorGlucose records for {Source}", source);
            return false;
        }
    }

    public async Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        // The v1 entries collection spans CGM readings (sensor glucose) and manual BG
        // checks (meter glucose), so the resume watermark is the latest of either —
        // scoped to THIS source. Never fall back across sources: when another uploader
        // is already writing glucose (e.g. Trio pushing directly while a Nightscout
        // migration runs), a cross-source "current entry" mis-classifies the
        // connector's first-ever sync as incremental and skips its full-history
        // backfill.
        var sgTimestamp = await _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);
        var mgTimestamp = await _meterGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);

        if (sgTimestamp.HasValue && mgTimestamp.HasValue)
            return sgTimestamp.Value > mgTimestamp.Value ? sgTimestamp.Value : mgTimestamp.Value;

        return sgTimestamp ?? mgTimestamp;
    }

    public async Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        return await _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);
    }

}
