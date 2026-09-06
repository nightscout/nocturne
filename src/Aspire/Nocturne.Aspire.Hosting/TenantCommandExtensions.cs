using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Adds Aspire dashboard commands for tenant management via the dev-only
/// admin API: list, create, delete/reset.
/// </summary>
public static class TenantCommandExtensions
{
    private const string SnapshotRelativePath = "docs/seed/dev-snapshot.json";

    public static IResourceBuilder<PostgresServerResource> WithListTenantsCommand(
        this IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ProjectResource> api)
    {
        postgres.WithCommand(
            name: "list-tenants",
            displayName: "List Tenants",
            executeCommand: context => OnListTenantsAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "People",
                IconVariant = IconVariant.Filled,
            });

        return postgres;
    }

    public static IResourceBuilder<PostgresServerResource> WithDeleteTenantCommand(
        this IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ProjectResource> api)
    {
        postgres.WithCommand(
            name: "delete-tenant",
            displayName: "Delete Tenants",
            executeCommand: context => OnDeleteTenantAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "PersonDelete",
                IconVariant = IconVariant.Filled,
            });

        return postgres;
    }

    public static IResourceBuilder<PostgresServerResource> WithCreateTenantCommand(
        this IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ProjectResource> api)
    {
        postgres.WithCommand(
            name: "create-tenant",
            displayName: "Create Tenant",
            executeCommand: context => OnCreateTenantAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "PersonAdd",
                IconVariant = IconVariant.Filled,
            });

        return postgres;
    }

    public static IResourceBuilder<PostgresServerResource> WithSeedTenantCommand(
        this IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ProjectResource> api)
    {
        postgres.WithCommand(
            name: "seed-tenant",
            displayName: "Seed Tenant (loginable)",
            executeCommand: context => OnSeedTenantAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "PersonKey",
                IconVariant = IconVariant.Filled,
            });

        return postgres;
    }

    // -----------------------------------------------------------------
    // List Tenants
    // -----------------------------------------------------------------

    private static async Task<ExecuteCommandResult> OnListTenantsAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();

        try
        {
            var endpoint = api.GetEndpoint("http");
            if (!endpoint.IsAllocated)
                return CommandResults.Failure("API endpoint is not yet allocated.");

            using var http = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

            var response = await http.GetAsync(
                "api/v4/dev-only/admin/tenants",
                context.CancellationToken);

            response.EnsureSuccessStatusCode();

            var tenants = await response.Content.ReadFromJsonAsync<List<TenantSummary>>(
                JsonOptions, context.CancellationToken);

            if (tenants is null or { Count: 0 })
                return CommandResults.Success("No tenants found", "");

            var md = new StringBuilder();

            foreach (var t in tenants)
            {
                var flags = new List<string>();
                if (!t.IsActive) flags.Add("inactive");
                var flagStr = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";

                md.AppendLine($"### {t.Slug}{flagStr}");
                md.AppendLine($"**{t.DisplayName}** | {t.Members} members");
                md.AppendLine();
                md.AppendLine($"| Entries | Treatments | Device Statuses | Profiles |");
                md.AppendLine($"|---------|------------|-----------------|----------|");
                md.AppendLine($"| {t.Entries:N0} | {t.Treatments:N0} | {t.DeviceStatuses:N0} | {t.Profiles} |");
                md.AppendLine();

                if (t.LatestEntry is { } latest)
                    md.AppendLine($"Latest entry: {latest:yyyy-MM-dd HH:mm} UTC");

                if (t.Connectors is { Count: > 0 })
                {
                    md.AppendLine();
                    foreach (var c in t.Connectors)
                    {
                        var health = c.IsHealthy ? "OK" : "ERROR";
                        var sync = c.LastSuccessfulSync is { } s
                            ? $"last sync {s:yyyy-MM-dd HH:mm} UTC"
                            : "never synced";
                        md.AppendLine($"- **{c.Name}** [{health}] — {sync}");
                        if (!c.IsHealthy && c.LastError is { Length: > 0 } err)
                            md.AppendLine($"  `{Truncate(err, 120)}`");
                    }
                }

                md.AppendLine();
                md.AppendLine("---");
                md.AppendLine();
            }

            return CommandResults.Success(
                $"{tenants.Count} tenant(s)",
                new CommandResultData
                {
                    Value = md.ToString(),
                    Format = CommandResultFormat.Markdown,
                    DisplayImmediately = true,
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list tenants");
            return CommandResults.Failure(ex.Message);
        }
    }

    // -----------------------------------------------------------------
    // Delete / Reset Tenant
    // -----------------------------------------------------------------

    private static async Task<ExecuteCommandResult> OnDeleteTenantAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();
        var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();

        try
        {
            var endpoint = api.GetEndpoint("http");
            if (!endpoint.IsAllocated)
                return CommandResults.Failure("API endpoint is not yet allocated.");

            using var http = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

            // Fetch tenants
            var response = await http.GetAsync(
                "api/v4/dev-only/admin/tenants",
                context.CancellationToken);
            response.EnsureSuccessStatusCode();

            var tenants = await response.Content.ReadFromJsonAsync<List<TenantSummary>>(
                JsonOptions, context.CancellationToken);

            if (tenants is null or { Count: 0 })
                return CommandResults.Failure("No tenants found.");

            // One checkbox per tenant. Selection order matches `tenants`, so the
            // result is read back positionally.
            var inputs = tenants.Select(t => new InteractionInput
            {
                Name = $"{t.Slug} — {t.Entries:N0} entries, {t.Treatments:N0} treatments",
                InputType = InputType.Boolean,
                Required = false,
            }).ToList();

            var pickResult = await interactionService.PromptInputsAsync(
                "Delete Tenants",
                "Check the tenants to permanently delete, then confirm. This destroys all of their data.",
                inputs);

            if (pickResult.Canceled)
                return CommandResults.Canceled();

            var selected = tenants
                .Where((_, i) => bool.TryParse(pickResult.Data[i].Value, out var check) && check)
                .ToList();

            if (selected.Count == 0)
                return CommandResults.Success("No tenants selected", "");

            var deleted = new List<string>();
            var failed = new List<string>();

            foreach (var t in selected)
            {
                logger.LogInformation("Deleting tenant '{Slug}' ({Id})...", t.Slug, t.Id);

                var deleteResponse = await http.DeleteAsync(
                    $"api/v4/dev-only/admin/tenants/{t.Id}",
                    context.CancellationToken);

                if (deleteResponse.IsSuccessStatusCode)
                {
                    logger.LogInformation("Tenant '{Slug}' deleted successfully", t.Slug);
                    deleted.Add(t.Slug);
                }
                else
                {
                    var error = await deleteResponse.Content.ReadAsStringAsync(context.CancellationToken);
                    logger.LogError("Failed to delete tenant '{Slug}': {Status} {Error}",
                        t.Slug, deleteResponse.StatusCode, error);
                    failed.Add($"{t.Slug} ({deleteResponse.StatusCode})");
                }
            }

            var md = new StringBuilder();
            if (deleted.Count > 0)
                md.AppendLine($"Deleted: {string.Join(", ", deleted)}");
            if (failed.Count > 0)
                md.AppendLine($"Failed: {string.Join(", ", failed)}");

            var summary = failed.Count == 0
                ? $"{deleted.Count} tenant(s) deleted"
                : $"{deleted.Count} deleted, {failed.Count} failed";

            return failed.Count > 0 && deleted.Count == 0
                ? CommandResults.Failure(md.ToString())
                : CommandResults.Success(
                    summary,
                    new CommandResultData
                    {
                        Value = md.ToString(),
                        Format = CommandResultFormat.Markdown,
                        DisplayImmediately = true,
                    });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete tenant");
            return CommandResults.Failure(ex.Message);
        }
    }

    // -----------------------------------------------------------------
    // Create Tenant
    // -----------------------------------------------------------------

    private static async Task<ExecuteCommandResult> OnCreateTenantAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();
        var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();

        // Check if snapshot exists
        var snapshotPath = ResolveSnapshotPath();
        var snapshotSlugs = GetSnapshotTenantSlugs(snapshotPath);

        // Prompt for tenant details
        var inputs = new List<InteractionInput>
        {
            new()
            {
                Name = "Slug",
                InputType = InputType.Text,
                Required = true,
                Placeholder = "my-tenant",
            },
            new()
            {
                Name = "Display Name",
                InputType = InputType.Text,
                Required = true,
                Placeholder = "My Tenant",
            },
        };

        if (snapshotSlugs.Count > 0)
        {
            inputs.Add(new InteractionInput
            {
                Name = "Initialize with snapshot",
                Description = $"Available slugs in snapshot: {string.Join(", ", snapshotSlugs)}",
                InputType = InputType.Boolean,
                Required = false,
            });
        }

        var result = await interactionService.PromptInputsAsync(
            "Create Tenant",
            "Enter the details for the new tenant:",
            inputs);

        if (result.Canceled)
            return CommandResults.Canceled();

        var slug = result.Data[0].Value?.Trim();
        var displayName = result.Data[1].Value?.Trim();
        var initWithSnapshot = inputs.Count > 2
            && bool.TryParse(result.Data[2].Value, out var snap) && snap;

        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(displayName))
            return CommandResults.Failure("Slug and display name are required.");

        try
        {
            var endpoint = api.GetEndpoint("http");
            if (!endpoint.IsAllocated)
                return CommandResults.Failure("API endpoint is not yet allocated.");

            using var http = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

            logger.LogInformation("Creating tenant '{Slug}' ({DisplayName})...", slug, displayName);

            var response = await http.PostAsJsonAsync(
                "api/v4/dev-only/admin/tenants",
                new { slug, displayName },
                context.CancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(context.CancellationToken);
                return CommandResults.Failure($"{response.StatusCode}: {error}");
            }

            var tenant = await response.Content.ReadFromJsonAsync<JsonElement>(
                context.CancellationToken);

            var tenantId = tenant.GetProperty("id").GetGuid();

            logger.LogInformation(
                "Tenant '{Slug}' created successfully.",
                slug);

            // Import scoped snapshot if requested and matching slug exists
            if (initWithSnapshot && snapshotSlugs.Contains(slug))
            {
                var importResult = await ImportScopedSnapshotAsync(
                    http, tenantId, slug, snapshotPath, logger, context.CancellationToken);
                if (importResult is not null)
                    return importResult;
            }
            else if (initWithSnapshot)
            {
                logger.LogWarning(
                    "Snapshot checkbox was checked but no tenant with slug '{Slug}' found in snapshot — skipping",
                    slug);
            }

            var message = initWithSnapshot && snapshotSlugs.Contains(slug)
                ? $"Tenant '{slug}' created with snapshot data"
                : $"Tenant '{slug}' created";
            return CommandResults.Success(message, "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tenant");
            return CommandResults.Failure(ex.Message);
        }
    }

    // -----------------------------------------------------------------
    // Seed Tenant (loginable + optional sample data)
    // -----------------------------------------------------------------

    private static async Task<ExecuteCommandResult> OnSeedTenantAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();
        var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();

        var inputs = new List<InteractionInput>
        {
            new()
            {
                Name = "Slug",
                InputType = InputType.Text,
                Required = true,
                Value = "test123",
            },
            new()
            {
                Name = "Display Name",
                InputType = InputType.Text,
                Required = true,
                Value = "Test",
            },
            new()
            {
                Name = "Owner Username",
                InputType = InputType.Text,
                Required = true,
                Value = "dev",
            },
            new()
            {
                Name = "Sample data",
                Description = "Populate with realistic history (glucose, treatments, sleep, trackers, alerts).",
                InputType = InputType.Boolean,
                Required = false,
                Value = "true",
            },
            new()
            {
                Name = "Sample data days",
                Description = "Days of history to generate when sample data is enabled.",
                InputType = InputType.Number,
                Required = false,
                Value = "90",
            },
        };

        var result = await interactionService.PromptInputsAsync(
            "Seed Tenant",
            "Create a loginable tenant with an owner and (optionally) sample data. " +
            "Returns a login link that signs in as the owner.",
            inputs);

        if (result.Canceled)
            return CommandResults.Canceled();

        var slug = result.Data[0].Value?.Trim();
        var displayName = result.Data[1].Value?.Trim();
        var ownerUsername = result.Data[2].Value?.Trim();
        var sampleData = bool.TryParse(result.Data[3].Value, out var sd) && sd;
        var sampleDataDays = int.TryParse(result.Data[4].Value, out var days) && days > 0 ? days : 90;

        if (string.IsNullOrWhiteSpace(slug)
            || string.IsNullOrWhiteSpace(displayName)
            || string.IsNullOrWhiteSpace(ownerUsername))
            return CommandResults.Failure("Slug, display name, and owner username are required.");

        try
        {
            var endpoint = api.GetEndpoint("http");
            if (!endpoint.IsAllocated)
                return CommandResults.Failure("API endpoint is not yet allocated.");

            using var http = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

            logger.LogInformation(
                "Seeding tenant '{Slug}' (owner={Owner}, sampleData={SampleData})...",
                slug, ownerUsername, sampleData);

            var response = await http.PostAsJsonAsync(
                "api/v4/dev-only/admin/seed-tenant",
                new { slug, displayName, ownerUsername, sampleData, sampleDataDays },
                context.CancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(context.CancellationToken);
                return CommandResults.Failure($"{response.StatusCode}: {error}");
            }

            var seeded = await response.Content.ReadFromJsonAsync<SeedTenantResult>(
                JsonOptions, context.CancellationToken);

            logger.LogInformation("Tenant '{Slug}' seeded successfully", slug);

            var md = new StringBuilder();
            md.AppendLine($"### {slug} seeded");
            md.AppendLine();
            if (!string.IsNullOrEmpty(seeded?.LoginLink))
            {
                md.AppendLine($"**Login link:** [{seeded.LoginLink}]({seeded.LoginLink})");
                md.AppendLine();
                md.AppendLine("Opens the tenant UI signed in as the owner.");
                md.AppendLine();
            }
            if (sampleData && seeded is not null)
            {
                md.AppendLine($"| Entries | Treatments | Sleep sessions |");
                md.AppendLine($"|---------|------------|----------------|");
                md.AppendLine(
                    $"| {seeded.EntriesSeeded:N0} | {seeded.TreatmentsSeeded:N0} | {seeded.SleepSessionsSeeded:N0} |");
            }

            return CommandResults.Success(
                $"Tenant '{slug}' seeded",
                new CommandResultData
                {
                    Value = md.ToString(),
                    Format = CommandResultFormat.Markdown,
                    DisplayImmediately = true,
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed tenant");
            return CommandResults.Failure(ex.Message);
        }
    }

    // -----------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads the snapshot file, finds the tenant by slug, and POSTs it to
    /// the scoped import endpoint. Returns null on success, or a failure result.
    /// </summary>
    private static async Task<ExecuteCommandResult?> ImportScopedSnapshotAsync(
        HttpClient http, Guid tenantId, string slug, string snapshotPath,
        ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Importing scoped snapshot for '{Slug}'...", slug);

        var json = await File.ReadAllTextAsync(snapshotPath, ct);
        var doc = JsonDocument.Parse(json);
        var tenantsArray = doc.RootElement.GetProperty("tenants");

        JsonElement? match = null;
        foreach (var t in tenantsArray.EnumerateArray())
        {
            if (t.GetProperty("tenant").GetProperty("slug").GetString() == slug)
            {
                match = t;
                break;
            }
        }

        if (match is null)
        {
            logger.LogWarning("No tenant with slug '{Slug}' in snapshot — skipping import", slug);
            return null;
        }

        var content = new StringContent(
            match.Value.GetRawText(), Encoding.UTF8, "application/json");

        var importResponse = await http.PostAsync(
            $"api/v4/dev-only/admin/tenants/{tenantId}/import-snapshot",
            content, ct);

        if (!importResponse.IsSuccessStatusCode)
        {
            var error = await importResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("Scoped snapshot import failed: {Error}", error);
            return CommandResults.Failure($"Snapshot import failed: {error}");
        }

        logger.LogInformation("Scoped snapshot imported for '{Slug}'", slug);
        return null;
    }

    /// <summary>
    /// Reads the snapshot file and returns the set of tenant slugs it contains.
    /// Returns empty if the file doesn't exist or can't be parsed.
    /// </summary>
    private static HashSet<string> GetSnapshotTenantSlugs(string snapshotPath)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(snapshotPath))
            return slugs;

        try
        {
            var json = File.ReadAllText(snapshotPath);
            var doc = JsonDocument.Parse(json);
            foreach (var t in doc.RootElement.GetProperty("tenants").EnumerateArray())
            {
                var slug = t.GetProperty("tenant").GetProperty("slug").GetString();
                if (slug is not null)
                    slugs.Add(slug);
            }
        }
        catch
        {
            // Malformed snapshot — treat as empty
        }

        return slugs;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 3), "...");

    private static ResourceCommandState OnHealthyState(UpdateCommandStateContext context)
    {
        return context.ResourceSnapshot.HealthStatus is HealthStatus.Healthy
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }

    /// <summary>
    /// Walks up from the runtime base directory to find the repository root,
    /// then returns the absolute path to the snapshot file.
    /// </summary>
    private static string ResolveSnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "seed");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "dev-snapshot.json");

            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), SnapshotRelativePath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record TenantSummary(
        Guid Id, string Slug, string DisplayName, bool IsActive,
        string Timezone, DateTime CreatedAt, long Entries, long Treatments,
        long DeviceStatuses, int Profiles, int Members, DateTime? LatestEntry,
        List<ConnectorSummary> Connectors);

    private sealed record ConnectorSummary(
        string Name, bool IsHealthy, DateTime? LastSuccessfulSync, string? LastError);

    private sealed record SeedTenantResult(
        Guid TenantId, string? Url, string? LoginLink,
        int EntriesSeeded, int TreatmentsSeeded, int SleepSessionsSeeded);
}
