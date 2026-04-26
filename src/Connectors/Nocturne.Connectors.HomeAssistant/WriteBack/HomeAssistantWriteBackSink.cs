using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.HomeAssistant.Configurations;
using Nocturne.Connectors.HomeAssistant.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.HomeAssistant.WriteBack;

/// <summary>
/// Pushes Nocturne data to Home Assistant entities when new glucose entries arrive.
/// Implements IDataEventSink&lt;Entry&gt; to piggyback on glucose domain events.
/// </summary>
public class HomeAssistantWriteBackSink(
    IHomeAssistantApiClient apiClient,
    HomeAssistantConnectorConfiguration config,
    IApsSnapshotRepository apsSnapshotRepository,
    ILogger<HomeAssistantWriteBackSink> logger) : IDataEventSink<Entry>
{
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Cache the APS snapshot for the duration of one write-back cycle
    private ApsSnapshot? _cachedApsSnapshot;
    private bool _apsSnapshotFetched;

    public async Task OnCreatedAsync(Entry item, CancellationToken ct = default)
    {
        if (!config.WriteBackEnabled)
            return;

        // Prevent sync loop: HA → Nocturne → write-back → HA → repeat
        if (item.DataSource == DataSources.HomeAssistantConnector)
            return;

        if (IsStale(item))
            return;

        // Reset cache for this write-back cycle
        _apsSnapshotFetched = false;
        _cachedApsSnapshot = null;

        if (config.WriteBackTypes.Contains(WriteBackDataType.Glucose))
            await PushGlucoseAsync(item, ct);

        if (config.WriteBackTypes.Contains(WriteBackDataType.Iob))
            await PushIobAsync(ct);

        if (config.WriteBackTypes.Contains(WriteBackDataType.Cob))
            await PushCobAsync(ct);

        if (config.WriteBackTypes.Contains(WriteBackDataType.PredictedBg))
            await PushPredictedBgAsync(ct);

        if (config.WriteBackTypes.Contains(WriteBackDataType.LoopStatus))
            await PushLoopStatusAsync(ct);
    }

    public async Task OnCreatedAsync(IReadOnlyList<Entry> items, CancellationToken ct = default)
    {
        if (!config.WriteBackEnabled || items.Count == 0)
            return;

        var latest = items.MaxBy(e => e.Mills);
        if (latest != null)
            await OnCreatedAsync(latest, ct);
    }

    private static bool IsStale(Entry entry)
    {
        var entryTime = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills);
        return DateTimeOffset.UtcNow - entryTime > StalenessThreshold;
    }

    private async Task PushGlucoseAsync(Entry entry, CancellationToken ct)
    {
        try
        {
            var attributes = new Dictionary<string, object>
            {
                ["unit_of_measurement"] = "mg/dL",
                ["device_class"] = "blood_glucose",
                ["friendly_name"] = "Nocturne Glucose",
                ["icon"] = "mdi:diabetes",
                ["trend"] = entry.Direction ?? "Unknown",
                ["last_updated"] = DateTimeOffset.UtcNow.ToString("o")
            };

            await apiClient.SetStateAsync("sensor.nocturne_glucose",
                entry.Sgv?.ToString() ?? "0", attributes, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push glucose to HA");
        }
    }

    private async Task PushIobAsync(CancellationToken ct)
    {
        try
        {
            var aps = await GetLatestApsSnapshotAsync(ct);
            var iob = aps?.Iob;
            if (iob == null) return;

            var attributes = new Dictionary<string, object>
            {
                ["unit_of_measurement"] = "U",
                ["friendly_name"] = "Nocturne IOB",
                ["icon"] = "mdi:needle",
                ["last_updated"] = DateTimeOffset.UtcNow.ToString("o")
            };

            await apiClient.SetStateAsync("sensor.nocturne_iob",
                iob.Value.ToString("F2"), attributes, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push IOB to HA");
        }
    }

    private async Task PushCobAsync(CancellationToken ct)
    {
        try
        {
            var aps = await GetLatestApsSnapshotAsync(ct);
            var cob = aps?.Cob;
            if (cob == null) return;

            var attributes = new Dictionary<string, object>
            {
                ["unit_of_measurement"] = "g",
                ["friendly_name"] = "Nocturne COB",
                ["icon"] = "mdi:food-apple",
                ["last_updated"] = DateTimeOffset.UtcNow.ToString("o")
            };

            await apiClient.SetStateAsync("sensor.nocturne_cob",
                cob.Value.ToString("F1"), attributes, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push COB to HA");
        }
    }

    private async Task PushPredictedBgAsync(CancellationToken ct)
    {
        try
        {
            var aps = await GetLatestApsSnapshotAsync(ct);
            // Parse predicted values from APS snapshot's default prediction curve
            var predictedJson = aps?.PredictedDefaultJson;
            double[]? predicted = null;
            if (!string.IsNullOrEmpty(predictedJson))
            {
                try { predicted = JsonSerializer.Deserialize<double[]>(predictedJson, JsonOptions); }
                catch (JsonException) { /* ignore malformed prediction data */ }
            }
            if (predicted == null || predicted.Length == 0) return;

            // Last predicted value = eventual BG
            var eventualBg = predicted[^1];

            var attributes = new Dictionary<string, object>
            {
                ["unit_of_measurement"] = "mg/dL",
                ["device_class"] = "blood_glucose",
                ["friendly_name"] = "Nocturne Predicted BG",
                ["icon"] = "mdi:crystal-ball",
                ["prediction_points"] = predicted.Length,
                ["last_updated"] = DateTimeOffset.UtcNow.ToString("o")
            };

            await apiClient.SetStateAsync("sensor.nocturne_predicted_bg",
                eventualBg.ToString("F0"), attributes, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push predicted BG to HA");
        }
    }

    private async Task PushLoopStatusAsync(CancellationToken ct)
    {
        try
        {
            var aps = await GetLatestApsSnapshotAsync(ct);

            var state = aps?.Enacted == true ? "enacted" : (aps != null ? "open" : "unknown");

            var attributes = new Dictionary<string, object>
            {
                ["friendly_name"] = "Nocturne Loop Status",
                ["icon"] = "mdi:sync",
                ["last_updated"] = DateTimeOffset.UtcNow.ToString("o")
            };

            if (aps?.Enacted == true)
            {
                if (aps.EnactedRate != null)
                    attributes["enacted_rate"] = aps.EnactedRate;
                if (aps.EnactedDuration != null)
                    attributes["enacted_duration"] = aps.EnactedDuration;
            }

            await apiClient.SetStateAsync("sensor.nocturne_loop_status",
                state, attributes, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push loop status to HA");
        }
    }

    private async Task<ApsSnapshot?> GetLatestApsSnapshotAsync(CancellationToken ct)
    {
        if (!_apsSnapshotFetched)
        {
            var snapshots = await apsSnapshotRepository.GetAsync(
                from: null, to: null, device: null, source: null,
                limit: 1, offset: 0, descending: true, ct: ct);
            _cachedApsSnapshot = snapshots.FirstOrDefault();
            _apsSnapshotFetched = true;
        }
        return _cachedApsSnapshot;
    }
}
