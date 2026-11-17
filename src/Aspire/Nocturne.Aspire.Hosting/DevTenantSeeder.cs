using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Seeds the <c>dev</c> tenant with sample data on an instance that holds no tenant yet, so a
/// fresh clone or worktree opens on a populated dashboard instead of the first-run wizard.
/// </summary>
public sealed class DevTenantSeeder(HttpClient api, ILogger logger)
{
    public const string Slug = "dev";
    public const string DisplayName = "Dev";
    public const string OwnerUsername = "dev";

    /// <summary>
    /// An apex path that tenant resolution handles but the setup gate lets through
    /// (<c>[AllowDuringSetup]</c>), so its 503 <c>setup_required</c> means no tenant exists
    /// (demo tenants aside) and never that a sole tenant has yet to register an owner.
    /// <c>/api/v4/status</c> cannot tell: on the apex it answers 200 "setup_required" both with
    /// no tenant and with several.
    /// </summary>
    public const string ProbePath = "api/Metadata/auth-error-codes";

    /// <summary>Whether the instance holds no tenant, the only case it seeds.</summary>
    public async Task<bool> NeedsSeedAsync(CancellationToken ct)
    {
        using var response = await api.GetAsync(ProbePath, ct);
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            return false;

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() == "setup_required";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Seeds when <see cref="NeedsSeedAsync"/> says so. A failure is logged and swallowed: the
    /// stack still comes up, on the first-run wizard.
    /// </summary>
    public async Task RunAsync(int sampleDataDays, CancellationToken ct)
    {
        try
        {
            if (!await NeedsSeedAsync(ct))
            {
                logger.LogInformation("Tenants exist, skipping the dev tenant seed.");
                return;
            }

            logger.LogInformation(
                "No tenants exist. Seeding tenant '{Slug}' with {Days} days of sample data...",
                Slug, sampleDataDays);
            var elapsed = Stopwatch.StartNew();

            var (seeded, error) = await TenantSeedClient.SeedAsync(
                api,
                new SeedTenantRequest(Slug, DisplayName, OwnerUsername, SampleData: true, sampleDataDays),
                ct);

            if (error is not null)
            {
                logger.LogError("Seeding tenant '{Slug}' failed: {Error}", Slug, error);
                return;
            }

            logger.LogInformation(
                "Seeded tenant '{Slug}' in {Seconds:N1}s ({Entries:N0} entries, {Treatments:N0} treatments). Open {Url}",
                Slug, elapsed.Elapsed.TotalSeconds, seeded?.EntriesSeeded, seeded?.TreatmentsSeeded, seeded?.Url);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Seeding tenant '{Slug}' failed: the API did not answer.", Slug);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Seeding tenant '{Slug}' failed: the API timed out.", Slug);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Seeding tenant '{Slug}' failed: the API's answer was not readable.", Slug);
        }
    }
}
