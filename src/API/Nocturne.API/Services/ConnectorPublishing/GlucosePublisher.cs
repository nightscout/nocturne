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
    private readonly IAlertOrchestrator _alertOrchestrator;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<GlucosePublisher> _logger;

    public GlucosePublisher(
        IEntryService entryService,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IPatientDeviceStamper patientDeviceStamper,
        IDbContextFactory<NocturneDbContext> contextFactory,
        ITenantAccessor tenantAccessor,
        IAlertOrchestrator alertOrchestrator,
        IAuditContext auditContext,
        ILogger<GlucosePublisher> logger)
    {
        _entryService = entryService ?? throw new ArgumentNullException(nameof(entryService));
        _sensorGlucoseRepository = sensorGlucoseRepository ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _alertOrchestrator = alertOrchestrator ?? throw new ArgumentNullException(nameof(alertOrchestrator));
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
            await EvaluateAlertsForEntriesAsync(entryList, cancellationToken);
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
            await EvaluateAlertsForSensorGlucoseAsync(recordList, cancellationToken);

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

    /// <summary>
    /// Build a SensorContext from the most recent Entry and evaluate alert rules.
    /// </summary>
    private async Task EvaluateAlertsForEntriesAsync(List<Entry> entries, CancellationToken ct)
    {
        try
        {
            var latest = entries
                .Where(e => e.Sgv.HasValue && e.Sgv.Value > 0)
                .OrderByDescending(e => e.Mills)
                .FirstOrDefault();

            if (latest is null) return;

            var context = new SensorContext
            {
                LatestValue = (decimal?)latest.Sgv,
                LatestTimestamp = latest.Date ?? DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime,
                TrendRate = (decimal?)latest.TrendRate,
                LastReadingAt = latest.Date ?? DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime,
            };

            await _alertOrchestrator.EvaluateAsync(context, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert evaluation failed after entry publish");
        }
    }

    /// <summary>
    /// Build a SensorContext from the most recent SensorGlucose record and evaluate alert rules.
    /// </summary>
    private async Task EvaluateAlertsForSensorGlucoseAsync(List<SensorGlucose> records, CancellationToken ct)
    {
        try
        {
            var latest = records
                .Where(r => r.Mgdl > 0)
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefault();

            if (latest is null) return;

            var context = new SensorContext
            {
                LatestValue = (decimal)latest.Mgdl,
                LatestTimestamp = latest.Timestamp,
                TrendRate = (decimal?)latest.TrendRate,
                LastReadingAt = latest.Timestamp,
            };

            await _alertOrchestrator.EvaluateAsync(context, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert evaluation failed after SensorGlucose publish");
        }
    }
}
