using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Adds Aspire dashboard commands for controlling the Demo Data Service lifecycle:
/// provision, pause, resume, stop, wipe, and reconfigure.
/// </summary>
public static class DemoCommandExtensions
{
    public static IResourceBuilder<T> WithDemoCommands<T>(this IResourceBuilder<T> resource)
        where T : IResourceWithEndpoints
    {
        resource.WithCommand(
            name: "demo-provision",
            displayName: "Provision",
            executeCommand: context => ExecuteDemoCommandAsync(resource, "provision", context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "Play",
                IconVariant = IconVariant.Filled,
            });

        resource.WithCommand(
            name: "demo-pause",
            displayName: "Pause",
            executeCommand: context => ExecuteDemoCommandAsync(resource, "pause", context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "Pause",
                IconVariant = IconVariant.Filled,
            });

        resource.WithCommand(
            name: "demo-resume",
            displayName: "Resume",
            executeCommand: context => ExecuteDemoCommandAsync(resource, "resume", context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "Play",
                IconVariant = IconVariant.Filled,
            });

        resource.WithCommand(
            name: "demo-stop",
            displayName: "Stop",
            executeCommand: context => ExecuteDemoCommandAsync(resource, "stop", context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "Stop",
                IconVariant = IconVariant.Filled,
            });

        resource.WithCommand(
            name: "demo-wipe",
            displayName: "Wipe Data",
            executeCommand: context => ExecuteDemoCommandAsync(resource, "wipe", context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "Delete",
                IconVariant = IconVariant.Filled,
            });

        resource.WithCommand(
            name: "demo-reconfigure",
            displayName: "Reconfigure",
            executeCommand: context => ExecuteDemoCommandAsync(resource, "reconfigure", context),
            commandOptions: new CommandOptions
            {
                UpdateState = OnHealthyState,
                IconName = "ArrowSync",
                IconVariant = IconVariant.Filled,
            });

        return resource;
    }

    private static async Task<ExecuteCommandResult> ExecuteDemoCommandAsync<T>(
        IResourceBuilder<T> resource,
        string command,
        ExecuteCommandContext context)
        where T : IResourceWithEndpoints
    {
        var logger = context.Services.GetRequiredService<ILogger<ProjectResource>>();

        try
        {
            var endpoint = resource.GetEndpoint("http");
            if (!endpoint.IsAllocated)
                return CommandResults.Failure("Demo service HTTP endpoint is not yet allocated.");

            using var http = new HttpClient { BaseAddress = new Uri(endpoint.Url) };

            logger.LogInformation("Sending {Command} to demo service at {Url}...", command, endpoint.Url);

            var response = await http.PostAsync($"/{command}", null, context.CancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(context.CancellationToken);
                logger.LogError("Demo service {Command} failed: {Status} {Error}", command, response.StatusCode, error);
                return CommandResults.Failure($"{response.StatusCode}: {error}");
            }

            logger.LogInformation("Demo service {Command} completed successfully", command);
            return CommandResults.Success($"Demo service: {command} completed", "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute demo service command '{Command}'", command);
            return CommandResults.Failure(ex.Message);
        }
    }

    private static ResourceCommandState OnHealthyState(UpdateCommandStateContext context)
    {
        return context.ResourceSnapshot.HealthStatus is HealthStatus.Healthy
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }
}
