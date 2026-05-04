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
            displayName: "Delete / Reset Tenant",
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

    // -----------------------------------------------------------------
    // List Tenants
    // -----------------------------------------------------------------

    private static async Task<ExecuteCommandResult> OnListTenantsAsync(
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

            var response = await http.GetAsync(
                "api/v4/dev-only/admin/tenants",
                context.CancellationToken);

            response.EnsureSuccessStatusCode();

            var tenants = await response.Content.ReadFromJsonAsync<List<TenantSummary>>(
                JsonOptions, context.CancellationToken);

            if (tenants is null or { Count: 0 })
            {
                await interactionService.PromptConfirmationAsync(
                    "Tenants",
                    "No tenants found.",
                    new MessageBoxInteractionOptions { ShowSecondaryButton = false });
                return CommandResults.Success();
            }

            var md = new StringBuilder();

            foreach (var t in tenants)
            {
                var flags = new List<string>();
                if (!t.IsActive) flags.Add("inactive");
                var flagStr = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";

                md.AppendLine($"### {t.Slug}{flagStr}");
                md.AppendLine($"**{t.DisplayName}** | {t.Timezone} | {t.Members} members");
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

            await interactionService.PromptConfirmationAsync(
                $"Tenants ({tenants.Count})",
                md.ToString(),
                new MessageBoxInteractionOptions
                {
                    ShowSecondaryButton = false,
                    EnableMessageMarkdown = true,
                });

            return CommandResults.Success();
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

            // Step 1: Pick a tenant
            var inputs = new List<InteractionInput>
            {
                new()
                {
                    Name = "Tenant",
                    InputType = InputType.Choice,
                    Required = true,
                    Options = tenants.Select(t =>
                    {
                        var label = $"{t.Slug} — {t.Entries:N0} entries, {t.Treatments:N0} treatments";
                        return KeyValuePair.Create(t.Id.ToString(), label);
                    }).ToList(),
                },
            };

            var pickResult = await interactionService.PromptInputsAsync(
                "Delete Tenant",
                "Select a tenant to permanently delete.",
                inputs);

            if (pickResult.Canceled)
                return CommandResults.Canceled();

            var selectedId = Guid.Parse(pickResult.Data[0].Value!);
            var selected = tenants.First(t => t.Id == selectedId);

            // Step 2: Confirm by typing the slug
            var confirmInputs = new List<InteractionInput>
            {
                new()
                {
                    Name = "Slug",
                    InputType = InputType.Text,
                    Required = true,
                    Placeholder = selected.Slug,
                },
            };

            var confirmResult = await interactionService.PromptInputsAsync(
                "Confirm Deletion",
                $"**{selected.DisplayName}** (`{selected.Slug}`) will be permanently deleted.\n\n" +
                $"This will destroy **{selected.Entries:N0}** entries, **{selected.Treatments:N0}** treatments, " +
                $"**{selected.DeviceStatuses:N0}** device statuses, and all other tenant data.\n\n" +
                $"Type **{selected.Slug}** to confirm:",
                confirmInputs);

            if (confirmResult.Canceled)
                return CommandResults.Canceled();

            var typedSlug = confirmResult.Data[0].Value?.Trim();
            if (!string.Equals(typedSlug, selected.Slug, StringComparison.OrdinalIgnoreCase))
                return CommandResults.Failure($"Slug mismatch: expected \"{selected.Slug}\", got \"{typedSlug}\". Aborted.");

            // Step 3: Delete
            logger.LogInformation("Deleting tenant '{Slug}' ({Id})...", selected.Slug, selected.Id);

            var deleteResponse = await http.DeleteAsync(
                $"api/v4/dev-only/admin/tenants/{selected.Id}",
                context.CancellationToken);

            if (!deleteResponse.IsSuccessStatusCode)
            {
                var error = await deleteResponse.Content.ReadAsStringAsync(context.CancellationToken);
                return CommandResults.Failure($"{deleteResponse.StatusCode}: {error}");
            }

            logger.LogInformation("Tenant '{Slug}' deleted successfully", selected.Slug);

            return CommandResults.Success();
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

            return CommandResults.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tenant");
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
}
