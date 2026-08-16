using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// HTTP client that communicates with the Nocturne API. Historical backfill is
/// seeded server-side through the demo admin endpoint; the realtime tick posts
/// the current entry, treatments, and device status through the V4 API,
/// exactly like a native uploader. Admin endpoints handle tenant provisioning
/// and lifecycle.
/// </summary>
public sealed class DemoApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DemoApiClient> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DemoApiClient(IHttpClientFactory httpClientFactory, ILogger<DemoApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Provisions the demo tenant via the internal admin endpoint.
    /// This is idempotent — if the tenant already exists, it returns the existing state.
    /// </summary>
    public async Task<DemoTenantState?> ProvisionAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        try
        {
            var response = await client.PostAsync("api/v4/admin/demo/provision", null, ct);
            response.EnsureSuccessStatusCode();
            var state = await response.Content.ReadFromJsonAsync<DemoTenantState>(SerializerOptions, ct);
            _logger.LogInformation("Demo tenant provisioned successfully");
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision demo tenant");
            return null;
        }
    }

    /// <summary>
    /// Gets current demo tenant status from the admin endpoint.
    /// </summary>
    public async Task<DemoTenantState?> GetStatusAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        try
        {
            var response = await client.GetAsync("api/v4/admin/demo/status", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<DemoTenantState>(SerializerOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get demo tenant status");
            return null;
        }
    }

    /// <summary>
    /// Posts the current CGM entry to the V4 sensor-glucose endpoint, routed to
    /// the demo tenant via Host header.
    /// </summary>
    public async Task PostCurrentEntryAsync(Entry entry, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoTenant");
        var payload = new[]
        {
            new
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills),
                device = entry.Device,
                dataSource = entry.DataSource,
                mgdl = entry.Sgv ?? entry.Mgdl,
                direction = Enum.TryParse<GlucoseDirection>(entry.Direction, out var dir) ? (int?)dir : null,
                delta = entry.Delta,
                noise = entry.Noise,
                filtered = entry.Filtered,
                unfiltered = entry.Unfiltered,
            },
        };
        var response = await client.PostAsJsonAsync("api/v4/glucose/sensor/bulk", payload, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Posted current entry via V4 API");
    }

    /// <summary>
    /// Posts the tick's treatments through the per-type V4 endpoints
    /// (temp basals, boluses, carb intakes).
    /// </summary>
    public async Task PostCurrentTreatmentsAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoTenant");
        foreach (var treatment in treatments)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills);

            if (treatment.EventType == "Temp Basal" && treatment.Rate is { } rate)
            {
                // The temp-basal endpoint is array-only (uploaders batch).
                var payload = new[]
                {
                    new
                    {
                        timestamp,
                        rate,
                        durationMinutes = treatment.Duration,
                        origin = (int)TempBasalOrigin.Algorithm,
                        device = DemoDeviceStatusGenerator.DeviceName,
                        dataSource = treatment.DataSource,
                    },
                };
                var response = await client.PostAsJsonAsync("api/v4/insulin/temp-basals", payload, SerializerOptions, ct);
                response.EnsureSuccessStatusCode();
            }
            else if (treatment.Insulin is > 0)
            {
                var response = await client.PostAsJsonAsync("api/v4/insulin/boluses", new
                {
                    timestamp,
                    insulin = treatment.Insulin.Value,
                    kind = (int)(treatment.EventType == "SMB" ? BolusKind.Algorithm : BolusKind.Manual),
                    automatic = treatment.EventType == "SMB",
                    device = DemoDeviceStatusGenerator.DeviceName,
                    dataSource = treatment.DataSource,
                }, SerializerOptions, ct);
                response.EnsureSuccessStatusCode();
            }
            else if (treatment.Carbs is > 0)
            {
                var response = await client.PostAsJsonAsync("api/v4/nutrition/carbs", new
                {
                    timestamp,
                    carbs = treatment.Carbs.Value,
                    dataSource = treatment.DataSource,
                }, SerializerOptions, ct);
                response.EnsureSuccessStatusCode();
            }
        }

        if (treatments.Count > 0)
            _logger.LogDebug("Posted {Count} treatments via V4 API", treatments.Count);
    }

    /// <summary>
    /// Posts the tick's device status as the V4 APS/pump/uploader snapshot
    /// triple, correlated like a decomposed legacy upload.
    /// </summary>
    public async Task PostDeviceStatusAsync(DeviceStatus status, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoTenant");
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(status.Mills);
        var correlationId = Guid.CreateVersion7();
        var suggested = status.OpenAps?.Suggested;
        var enacted = status.OpenAps?.Enacted;

        var apsPayload = new[]
        {
            new
            {
                timestamp,
                device = status.Device,
                dataSource = DataSources.DemoService,
                syncIdentifier = $"demo-aps-{status.Mills}",
                correlationId,
                aidAlgorithm = (int)AidAlgorithm.Trio,
                aidVersion = status.OpenAps?.Version,
                iob = status.OpenAps?.Iob?.Iob,
                bolusIob = status.OpenAps?.Iob?.BolusIob,
                basalIob = status.OpenAps?.Iob?.BasalIob,
                cob = status.OpenAps?.Cob,
                currentBg = suggested?.Bg,
                eventualBg = suggested?.EventualBG,
                targetBg = suggested?.TargetBG,
                recommendedBolus = suggested?.InsulinReq,
                sensitivityRatio = suggested?.SensitivityRatio,
                enacted = enacted is not null,
                enactedRate = enacted?.Rate,
                enactedDuration = enacted?.Duration,
                suggestedJson = Serialize(suggested),
                enactedJson = Serialize(enacted),
                predictedIobJson = Serialize(suggested?.PredBGs?.IOB),
                predictedZtJson = Serialize(suggested?.PredBGs?.ZT),
                predictedCobJson = Serialize(suggested?.PredBGs?.COB),
                predictedUamJson = Serialize(suggested?.PredBGs?.UAM),
                predictedStartTimestamp = timestamp,
            },
        };
        var apsResponse = await client.PostAsJsonAsync("api/v4/device-status/aps", apsPayload, SerializerOptions, ct);
        apsResponse.EnsureSuccessStatusCode();

        var pumpPayload = new[]
        {
            new
            {
                timestamp,
                device = status.Device,
                dataSource = DataSources.DemoService,
                syncIdentifier = $"demo-pump-{status.Mills}",
                correlationId,
                manufacturer = status.Pump?.Manufacturer,
                model = status.Pump?.Model,
                reservoir = status.Pump?.Reservoir,
                batteryPercent = status.Pump?.Battery?.Percent,
                bolusing = status.Pump?.Status?.Bolusing ?? false,
                suspended = status.Pump?.Status?.Suspended ?? false,
                pumpStatus = status.Pump?.Status?.Status,
                clock = status.Pump?.Clock,
            },
        };
        var pumpResponse = await client.PostAsJsonAsync("api/v4/device-status/pump", pumpPayload, SerializerOptions, ct);
        pumpResponse.EnsureSuccessStatusCode();

        var uploaderPayload = new[]
        {
            new
            {
                timestamp,
                device = status.Device,
                dataSource = DataSources.DemoService,
                syncIdentifier = $"demo-uploader-{status.Mills}",
                correlationId,
                battery = status.Uploader?.Battery,
                isCharging = status.Uploader?.IsCharging,
            },
        };
        var uploaderResponse = await client.PostAsJsonAsync("api/v4/device-status/uploader", uploaderPayload, SerializerOptions, ct);
        uploaderResponse.EnsureSuccessStatusCode();

        _logger.LogDebug("Posted device status triple via V4 API");
    }

    /// <summary>
    /// The most recent sensor-glucose value, used to continue the realtime
    /// stream from where the seeded history ended.
    /// </summary>
    public async Task<double?> GetLatestGlucoseAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoTenant");
        try
        {
            var response = await client.GetAsync("api/v4/glucose/sensor?limit=1", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var page = await response.Content.ReadFromJsonAsync<SensorGlucosePage>(SerializerOptions, ct);
            return page?.Data is [{ Mgdl: > 0 } latest, ..] ? latest.Mgdl : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read latest glucose for realtime continuity");
            return null;
        }
    }

    /// <summary>
    /// Resets the demo tenant via the admin endpoint, clearing its data and every
    /// configuration change a visitor made and returning it to a freshly provisioned
    /// state. The tenant keeps its id, slug and share token.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.PostAsync("api/v4/admin/demo/reset", null, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Reset demo tenant (data + configuration) via API");
    }

    /// <summary>
    /// Updates the demo tenant status (next reset time, last reset time, active state).
    /// </summary>
    public async Task UpdateStatusAsync(DateTime? nextResetAt = null, DateTime? lastResetAt = null, bool? isActive = null, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var payload = new { nextResetAt, lastResetAt, isActive };
        var response = await client.PatchAsJsonAsync("api/v4/admin/demo/status", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Seeds the full sample set server-side via the admin endpoint: glucose,
    /// treatments, device status, therapy profile, and every lifestyle type.
    /// One call replaces the old streamed v1 backfill.
    /// </summary>
    public async Task SeedAsync(int days, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.PostAsJsonAsync(
            "api/v4/admin/demo/seed-extras", new { days, includeGlucose = true }, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Seeded full demo sample set via API");
    }

    private static string? Serialize<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value, SerializerOptions);
}

/// <summary>Minimal projection of the V4 paginated sensor-glucose response.</summary>
internal sealed class SensorGlucosePage
{
    public List<SensorGlucoseItem>? Data { get; set; }
}

internal sealed class SensorGlucoseItem
{
    public double Mgdl { get; set; }
}

/// <summary>
/// Represents the state of the demo tenant as returned by the admin API.
/// </summary>
public sealed class DemoTenantState
{
    public string? TenantId { get; set; }
    public string? Hostname { get; set; }
    public bool IsActive { get; set; }
    public DateTime? NextResetAt { get; set; }
    public DateTime? LastResetAt { get; set; }
}
