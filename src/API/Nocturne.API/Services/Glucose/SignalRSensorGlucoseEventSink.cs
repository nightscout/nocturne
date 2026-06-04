using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.API.Services.Entries;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// <see cref="IDataEventSink{T}"/> implementation for V4 <see cref="SensorGlucose"/> records that
/// projects each reading into the legacy <see cref="Entry"/> shape and broadcasts it on the
/// real-time <c>entries</c> collection (plus a <c>dataUpdate</c>) via <see cref="IWriteSideEffects"/>.
/// </summary>
/// <remarks>
/// V4-native glucose ingestion — the <c>POST /api/v4/glucose/sensor</c> controller and the connector
/// <see cref="Nocturne.API.Services.ConnectorPublishing.GlucosePublisher"/> — writes directly to the
/// SensorGlucose repository, bypassing the legacy <see cref="SignalREntryEventSink"/>, so without this
/// sink those readings produce no real-time <c>create</c> event. Broadcasts as the <c>entries</c>
/// collection (what the web client and the SignalR→Socket.IO bridge subscribe to). Entries written via
/// the legacy path are decomposed to SensorGlucose by the decomposition pipeline, not this sink, so
/// there is no double broadcast. Cache invalidation and options mirror <see cref="SignalREntryEventSink"/>.
/// Failures are caught and logged; the write is never rolled back.
/// </remarks>
/// <seealso cref="IDataEventSink{T}"/>
/// <seealso cref="SignalREntryEventSink"/>
/// <seealso cref="SensorGlucoseToEntryMapper"/>
public class SignalRSensorGlucoseEventSink : IDataEventSink<SensorGlucose>
{
    private readonly IWriteSideEffects _sideEffects;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<SignalRSensorGlucoseEventSink> _logger;
    private const string CollectionName = "entries";

    private string TenantCacheId => _tenantAccessor.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    /// <summary>
    /// Initializes a new instance of <see cref="SignalRSensorGlucoseEventSink"/>.
    /// </summary>
    /// <param name="sideEffects">Orchestrates cache invalidation and real-time broadcasting.</param>
    /// <param name="tenantAccessor">Provides the current tenant context for scoping cache keys.</param>
    /// <param name="logger">The logger instance.</param>
    public SignalRSensorGlucoseEventSink(
        IWriteSideEffects sideEffects,
        ITenantAccessor tenantAccessor,
        ILogger<SignalRSensorGlucoseEventSink> logger)
    {
        _sideEffects = sideEffects;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    private WriteEffectOptions BuildWriteOptions() => new()
    {
        CacheKeysToRemove = [CacheKeyBuilder.BuildCurrentEntriesKey(TenantCacheId)],
        CachePatternsToClear = [CacheKeyBuilder.BuildRecentEntriesPattern(TenantCacheId)],
        DecomposeToV4 = false,
        BroadcastDataUpdate = true,
    };

    public Task OnCreatedAsync(SensorGlucose item, CancellationToken ct = default) =>
        OnCreatedAsync([item], ct);

    public async Task OnCreatedAsync(IReadOnlyList<SensorGlucose> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
            return;

        try
        {
            var entries = items.Select(SensorGlucoseToEntryMapper.ToEntry).ToList();
            await _sideEffects.OnCreatedAsync(CollectionName, entries, BuildWriteOptions(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process create side effects for {Count} sensor glucose readings", items.Count);
        }
    }
}
