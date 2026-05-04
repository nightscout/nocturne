using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Constants;
using Npgsql;
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
    private string? _postgresConnectionString;

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

        var appHost =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Nocturne_Aspire_Host>(
                [
                    "--environment=Testing",
                    "UseVolumes=false",
                    "PostgreSql:UseRemoteDatabase=false",
                ],
                configureBuilder: (appOptions, hostSettings) =>
                {
                    appOptions.DisableDashboard = true;
                }
            );

        _app = await appHost.BuildAsync();

        await _app.StartAsync();

        await WaitForResourceHealthyAsync(ServiceNames.NocturneApi, TimeSpan.FromSeconds(60));

        _apiClient = _app.CreateHttpClient(ServiceNames.NocturneApi, "api");

        _postgresConnectionString = await _app.GetConnectionStringAsync(ServiceNames.PostgreSql);
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
    /// Truncates all data tables to provide a clean slate between tests.
    /// Uses raw SQL against the Aspire-managed PostgreSQL instance.
    /// </summary>
    public async Task CleanupDatabaseAsync()
    {
        if (string.IsNullOrEmpty(_postgresConnectionString))
            return;

        await using var conn = new NpgsqlConnection(_postgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            TRUNCATE TABLE
                alert_deliveries,
                alert_instances,
                alert_excursions,
                alert_invites,
                alert_step_channels,
                alert_escalation_steps,
                alert_schedules,
                alert_rules,
                tracker_instances,
                tracker_presets,
                tracker_definitions,
                in_app_notifications,
                profiles,
                settings,
                foods,
                aps_snapshots,
                pump_snapshots,
                uploader_snapshots,
                device_status_extras,
                treatments,
                entries,
                oauth_refresh_tokens,
                oauth_authorization_codes,
                oauth_device_codes,
                oauth_grants,
                oauth_clients,
                auth_audit_logs
            CASCADE;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WaitForResourceHealthyAsync(string resourceName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
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
        return await _app!.GetConnectionStringAsync(resourceName);
    }
}
