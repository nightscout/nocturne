using System.Net.Http.Json;
using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>
/// The dev-only <c>seed-tenant</c> call, shared by the "Seed Tenant" dashboard command and the
/// dev tenant seed resource.
/// </summary>
public static class TenantSeedClient
{
    /// <summary>
    /// A month of sample data takes tens of seconds to write, past <see cref="HttpClient"/>'s
    /// default of 100 seconds once a longer history is asked for.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Posts <paramref name="request"/> and returns the seeded tenant, or the status and body of
    /// a refusal as <c>Error</c>.
    /// </summary>
    public static async Task<(SeedTenantResult? Result, string? Error)> SeedAsync(
        HttpClient api, SeedTenantRequest request, CancellationToken ct)
    {
        using var response = await api.PostAsJsonAsync(
            "api/v4/dev-only/admin/seed-tenant", request, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return (null, $"{response.StatusCode}: {error}");
        }

        return (await response.Content.ReadFromJsonAsync<SeedTenantResult>(JsonOptions, ct), null);
    }
}

public sealed record SeedTenantRequest(
    string Slug, string DisplayName, string OwnerUsername, bool SampleData, int SampleDataDays);

public sealed record SeedTenantResult(
    Guid TenantId, string? Url, string? LoginLink,
    int EntriesSeeded, int TreatmentsSeeded, int SleepSessionsSeeded);
