using Microsoft.EntityFrameworkCore;
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
internal sealed class GlucosePublisher : ConnectorPublisherBase, IGlucosePublisher
{
    private readonly IEntryService _entryService;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IMeterGlucoseRepository _meterGlucoseRepository;
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly ICanonicalAlertEvaluator _alertEvaluator;

    public GlucosePublisher(
        IEntryService entryService,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IMeterGlucoseRepository meterGlucoseRepository,
        IPatientDeviceStamper patientDeviceStamper,
        ICanonicalAlertEvaluator alertEvaluator,
        IAuditContext auditContext,
        ILogger<GlucosePublisher> logger)
        : base(auditContext, logger)
    {
        _entryService = entryService ?? throw new ArgumentNullException(nameof(entryService));
        _sensorGlucoseRepository = sensorGlucoseRepository ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _meterGlucoseRepository = meterGlucoseRepository ?? throw new ArgumentNullException(nameof(meterGlucoseRepository));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
        _alertEvaluator = alertEvaluator ?? throw new ArgumentNullException(nameof(alertEvaluator));
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
            Logger.LogError(ex, "Failed to publish entries for {Source}", source);
            return false;
        }
    }

    /// <remarks>
    /// Alert evaluation after the write is this publisher's one addition to the shared shape: a CGM
    /// reading is the trigger every glucose alert condition is written against.
    /// </remarks>
    public Task<bool> PublishSensorGlucoseAsync(
        IEnumerable<SensorGlucose> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(
            records, _sensorGlucoseRepository, source, origin, cancellationToken,
            beforeWrite: recordList => _patientDeviceStamper.StampAsync(
                recordList, DeviceAttributionCategories.SensorGlucose, source, cancellationToken),
            afterWrite: () => _alertEvaluator.EvaluateAsync(cancellationToken));

    /// <inheritdoc cref="ConnectorPublisherBase.LatestTimestampAsync" />
    /// <remarks>
    /// The v1 <c>entries</c> collection spans CGM readings (sensor glucose) and manual BG checks
    /// (meter glucose).
    /// </remarks>
    public Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
        => LatestTimestampAsync(
            () => _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _meterGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken));

    public async Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        return await _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);
    }

}
