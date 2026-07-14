using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// HTTP client that communicates with the Nocturne API to write demo data.
/// Uses the V1 entries/treatments endpoints for data ingestion and admin
/// endpoints for tenant provisioning and lifecycle management.
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
    /// Posts entries to the V1 entries endpoint, routed to the demo tenant via Host header.
    /// </summary>
    public async Task PostEntriesAsync(IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        var client = _httpClientFactory.CreateClient("DemoTenant");
        var response = await client.PostAsJsonAsync("api/v1/entries", entries, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Posted {Count} entries to API", entries.Count);
    }

    /// <summary>
    /// Posts treatments to the V1 treatments endpoint, routed to the demo tenant via Host header.
    /// </summary>
    public async Task PostTreatmentsAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct)
    {
        if (treatments.Count == 0)
            return;

        var client = _httpClientFactory.CreateClient("DemoTenant");
        var response = await client.PostAsJsonAsync("api/v1/treatments", treatments, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Posted {Count} treatments to API", treatments.Count);
    }

    /// <summary>
    /// Wipes all demo entries via the admin endpoint.
    /// </summary>
    public async Task WipeEntriesAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.DeleteAsync("api/v4/admin/demo/entries", ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Wiped demo entries via API");
    }

    /// <summary>
    /// Wipes all demo treatments via the admin endpoint.
    /// </summary>
    public async Task WipeTreatmentsAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.DeleteAsync("api/v4/admin/demo/treatments", ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Wiped demo treatments via API");
    }

    /// <summary>
    /// Wipes all demo data (entries + treatments) via the admin endpoint.
    /// </summary>
    public async Task WipeAllAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.DeleteAsync("api/v4/admin/demo/data", ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Wiped all demo data via API");
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
    /// Ensures the demo PatientInsulin record exists via the admin endpoint.
    /// </summary>
    public async Task EnsurePatientInsulinAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.PostAsync("api/v4/admin/demo/ensure-insulin", null, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Ensured demo PatientInsulin record exists");
    }

    /// <summary>
    /// Seeds the non-glucose sample set (device changes, sleep, heart rate,
    /// steps, trackers, alert rules + alarm history) via the admin endpoint.
    /// Runs server-side against the entries/treatments already posted.
    /// </summary>
    public async Task SeedExtrasAsync(int days, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DemoAdmin");
        var response = await client.PostAsJsonAsync(
            "api/v4/admin/demo/seed-extras", new { days }, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Seeded demo extras (sleep/activity/trackers/alerts) via API");
    }
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
