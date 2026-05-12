using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Adds Aspire dashboard commands to the PostgreSQL resource for exporting
/// and importing dev snapshots and triggering a full connector sync.
/// These are thin HTTP calls to the API's dev-only admin endpoints.
/// </summary>
public static class DevSnapshotCommandExtensions
{
    private const string SnapshotRelativePath = "docs/seed/dev-snapshot.json";

    public static IResourceBuilder<PostgresServerResource> WithDevSnapshotCommands(
        this IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ProjectResource> api)
    {
        postgres.WithCommand(
            name: "export-snapshot",
            displayName: "Export Dev Snapshot",
            executeCommand: context => OnExportSnapshotAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "DatabaseArrowDown",
                IconVariant = IconVariant.Filled,
            });

        postgres.WithCommand(
            name: "import-snapshot",
            displayName: "Import Dev Snapshot",
            executeCommand: context => OnImportSnapshotAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyAndSnapshotExistsState,
                IconName = "DatabaseArrowUp",
                IconVariant = IconVariant.Filled,
            });

        postgres.WithCommand(
            name: "sync-all-connectors",
            displayName: "Sync All Connectors",
            executeCommand: context => OnSyncAllConnectorsAsync(api, context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "ArrowSync",
                IconVariant = IconVariant.Filled,
            });

        return postgres;
    }

    // -----------------------------------------------------------------
    // Command handlers
    // -----------------------------------------------------------------

    private static async Task<ExecuteCommandResult> OnExportSnapshotAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();

        try
        {
            var baseUrl = GetApiBaseUrl(api);
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            logger.LogInformation("Exporting dev snapshot from {Url}...", baseUrl);

            var response = await http.GetAsync(
                "api/v4/dev-only/admin/snapshot",
                context.CancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(
                context.CancellationToken);

            var snapshotPath = ResolveSnapshotPath();
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);

            var prettyJson = JsonSerializer.Serialize(json, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            await File.WriteAllTextAsync(snapshotPath, prettyJson, context.CancellationToken);

            logger.LogInformation("Dev snapshot exported to {Path}", snapshotPath);
            return CommandResults.Success($"Exported to {snapshotPath}", "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export dev snapshot");
            return CommandResults.Failure(ex.Message);
        }
    }

    private static async Task<ExecuteCommandResult> OnImportSnapshotAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();

        try
        {
            var snapshotPath = ResolveSnapshotPath();

            if (!File.Exists(snapshotPath))
            {
                return CommandResults.Failure(
                    $"Snapshot file not found: {snapshotPath}");
            }

            var baseUrl = GetApiBaseUrl(api);
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            logger.LogInformation("Importing dev snapshot from {Path}...", snapshotPath);

            var json = await File.ReadAllTextAsync(snapshotPath, context.CancellationToken);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await http.PostAsync(
                "api/v4/dev-only/admin/snapshot",
                content,
                context.CancellationToken);

            response.EnsureSuccessStatusCode();

            logger.LogInformation("Dev snapshot imported successfully");
            return CommandResults.Success("Snapshot imported", "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import dev snapshot");
            return CommandResults.Failure(ex.Message);
        }
    }

    private static async Task<ExecuteCommandResult> OnSyncAllConnectorsAsync(
        IResourceBuilder<ProjectResource> api,
        ExecuteCommandContext context)
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<PostgresServerResource>>();

        try
        {
            var baseUrl = GetApiBaseUrl(api);
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            logger.LogInformation("Triggering sync of all connectors via {Url}...", baseUrl);

            var response = await http.PostAsync(
                "api/v4/dev-only/admin/sync-all",
                null,
                context.CancellationToken);

            response.EnsureSuccessStatusCode();

            logger.LogInformation("Connector sync triggered successfully");
            return CommandResults.Success("Connector sync triggered", "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger connector sync");
            return CommandResults.Failure(ex.Message);
        }
    }

    // -----------------------------------------------------------------
    // State callbacks
    // -----------------------------------------------------------------

    private static ResourceCommandState OnHealthyState(UpdateCommandStateContext context)
    {
        return context.ResourceSnapshot.HealthStatus is HealthStatus.Healthy
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }

    private static ResourceCommandState OnHealthyAndSnapshotExistsState(
        UpdateCommandStateContext context)
    {
        if (context.ResourceSnapshot.HealthStatus is not HealthStatus.Healthy)
            return ResourceCommandState.Disabled;

        var snapshotPath = ResolveSnapshotPath();
        return File.Exists(snapshotPath)
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Resolves the API's runtime HTTP base URL from its allocated endpoint.
    /// This only works in run mode after endpoints have been allocated.
    /// </summary>
    private static string GetApiBaseUrl(IResourceBuilder<ProjectResource> api)
    {
        var endpoint = api.GetEndpoint("http");

        if (!endpoint.IsAllocated)
        {
            throw new InvalidOperationException(
                "API endpoint is not yet allocated. Commands can only run after the resource is started.");
        }

        return endpoint.Url;
    }

    /// <summary>
    /// Walks up from the runtime base directory to find the repository root,
    /// then returns the absolute path to the snapshot file. Works for both
    /// the main checkout and git worktrees.
    /// </summary>
    private static string ResolveSnapshotPath()
    {
        // In Aspire run mode, AppContext.BaseDirectory is deep in a bin/
        // folder. Walk up until we find the docs/seed directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "seed");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, "dev-snapshot.json");
            }

            dir = dir.Parent;
        }

        // Fallback: assume current directory is the repo root.
        return Path.Combine(Directory.GetCurrentDirectory(), SnapshotRelativePath);
    }
}
