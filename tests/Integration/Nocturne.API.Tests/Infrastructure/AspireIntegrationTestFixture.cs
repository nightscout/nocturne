using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Aspire-based test fixture that manages the distributed application lifecycle.
/// Uses DistributedApplicationTestingBuilder to bootstrap the complete Aspire AppHost
/// with all dependencies (PostgreSQL, API, etc.) managed by Aspire.
/// </summary>
public class AspireIntegrationTestFixture : IAsyncLifetime
{
    private DistributedApplication? _app;
    private HttpClient? _apiClient;

    /// <summary>
    /// The distributed application instance managed by Aspire
    /// </summary>
    public DistributedApplication App =>
        _app
        ?? throw new InvalidOperationException("App not initialized. Call InitializeAsync first.");

    /// <summary>
    /// Pre-configured HttpClient for the Nocturne API service
    /// </summary>
    public HttpClient ApiClient =>
        _apiClient
        ?? throw new InvalidOperationException(
            "ApiClient not initialized. Call InitializeAsync first."
        );

    /// <summary>
    /// Creates an HttpClient for a specific resource in the Aspire application
    /// </summary>
    /// <param name="resourceName">Name of the resource (from ServiceNames)</param>
    /// <param name="endpointName">Optional endpoint name (e.g., "api" for the Nocturne API)</param>
    /// <returns>Configured HttpClient for the resource</returns>
    public HttpClient CreateHttpClient(string resourceName, string? endpointName = null)
    {
        return endpointName != null
            ? App.CreateHttpClient(resourceName, endpointName)
            : App.CreateHttpClient(resourceName);
    }

    public async Task InitializeAsync()
    {
        using var measurement = TestPerformanceTracker.MeasureTest(
            "AspireIntegrationTestFixture.Initialize"
        );

        // Create the Aspire application host using the testing builder
        // Pass arguments to configure test environment and disable volumes for containers
        var appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Nocturne_Aspire_Host>(
                [
                    "--environment=Testing",
                    "UseVolumes=false", // Disable persistent volumes for test isolation
                    "PostgreSql:UseRemoteDatabase=false", // Always use local container for tests
                ],
                configureBuilder: (appOptions, hostSettings) =>
                {
                    // Disable the dashboard during tests for faster startup
                    appOptions.DisableDashboard = true;
                }
            );

        // Build and start the distributed application
        _app = await appHost.BuildAsync();

        await _app.StartAsync();

        // Wait for the API to be ready before proceeding
        await WaitForResourceHealthyAsync(ServiceNames.NocturneApi, TimeSpan.FromSeconds(60));

        // Create and cache the API client for convenience
        // The API endpoint is named "api" in the AppHost configuration
        _apiClient = _app.CreateHttpClient(ServiceNames.NocturneApi, "api");
    }

    public async Task DisposeAsync()
    {
        using var measurement = TestPerformanceTracker.MeasureTest(
            "AspireIntegrationTestFixture.Dispose"
        );

        _apiClient?.Dispose();

        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Waits for a resource to become healthy/running
    /// </summary>
    private async Task WaitForResourceHealthyAsync(string resourceName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            // Wait for the resource to become healthy using the Aspire 9+ API
            await _app!.ResourceNotifications.WaitForResourceHealthyAsync(
                resourceName,
                cts.Token
            );
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Resource '{resourceName}' did not become healthy within {timeout.TotalSeconds} seconds."
            );
        }
    }

    /// <summary>
    /// Gets the connection string for a specific resource
    /// </summary>
    public async Task<string?> GetConnectionStringAsync(string resourceName)
    {
        // Use the Aspire 9+ GetConnectionStringAsync extension method
        return await _app!.GetConnectionStringAsync(resourceName);
    }
}
