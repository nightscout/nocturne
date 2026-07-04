using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Constants;
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
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ICanonicalAlertEvaluator _alertEvaluator;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<GlucosePublisher> _logger;

    public GlucosePublisher(
        IEntryService entryService,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IPatientDeviceStamper patientDeviceStamper,
        IDbContextFactory<NocturneDbContext> contextFactory,
        ITenantAccessor tenantAccessor,
        ICanonicalAlertEvaluator alertEvaluator,
        IAuditContext auditContext,
        ILogger<GlucosePublisher> logger)
    {
        _entryService = entryService ?? throw new ArgumentNullException(nameof(entryService));
        _sensorGlucoseRepository = sensorGlucoseRepository ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
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
            await _entryService.CreateEntriesAsync(entryList, cancellationToken);
            await UpdateLastReadingAtAsync(cancellationToken);
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
            await UpdateLastReadingAtAsync(cancellationToken);
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
        var sgTimestamp = await _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);
        if (sgTimestamp.HasValue)
            return sgTimestamp.Value;

        // Entry has no source column — only fall back for nightscout-connector.
        if (source == DataSources.NightscoutConnector)
        {
            var entry = await _entryService.GetCurrentEntryAsync(cancellationToken);
            if (entry == null)
                return null;

            if (entry.Date != default)
                return entry.Date;

            if (entry.Mills > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime;
        }

        return null;
    }

    public async Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        return await _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);
    }

    /// <summary>
    /// Updates the tenant's LastReadingAt timestamp after successful glucose publish.
    /// Uses ExecuteUpdateAsync to avoid materializing the tenant entity.
    /// </summary>
    private async Task UpdateLastReadingAtAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = _tenantAccessor.TenantId;
            if (tenantId == Guid.Empty) return;

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;
            await context.Tenants
                .Where(t => t.Id == tenantId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastReadingAt, now), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update tenant LastReadingAt timestamp");
        }
    }

}
