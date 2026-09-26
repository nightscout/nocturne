using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nocturne.Core.Constants;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Runs the API in-process on a real Kestrel port against a database of its own on
/// <see cref="SharedPostgres"/> (production role bootstrap, migrated schema), shared by every test
/// in the <c>ApiIntegration</c> collection.
/// </summary>
/// <remarks>
/// This used to boot the whole Aspire AppHost, which never completes on GitHub runners and
/// stands up far more than these tests talk to. The API runs as Development, as the AppHost ran
/// it, and listens on a real socket so HttpClient and SignalR connect exactly as external clients
/// do. <c>BASE_DOMAIN</c> is <c>localhost:{port}</c>, which is what
/// <see cref="AuthTestHelpers.GetBaseDomain"/> derives tenant hosts from.
/// </remarks>
public class ApiIntegrationTestFixture : IAsyncLifetime
{
    /// <summary>The instance key the API runs with.</summary>
    public const string InstanceKey = "test-secret-for-integration-tests";

    private WebApplicationFactory<Nocturne.API.Program>? _factory;
    private TestDatabase? _database;

    public string ApiBaseUrl { get; private set; } = string.Empty;

    /// <summary>A client for the API with no credentials.</summary>
    public HttpClient ApiClient { get; private set; } = null!;

    /// <summary>The running API's services.</summary>
    public IServiceProvider Services =>
        _factory?.Services ?? throw new InvalidOperationException("The API has not started.");

    /// <summary>
    /// A new client for <paramref name="resourceName"/>, which must be the API: it is the only
    /// service this fixture runs.
    /// </summary>
    public HttpClient CreateHttpClient(string resourceName, string? endpointName = null)
    {
        if (resourceName != ServiceNames.NocturneApi)
        {
            throw new ArgumentException($"Only {ServiceNames.NocturneApi} runs in this fixture, not {resourceName}.", nameof(resourceName));
        }

        return new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
    }

    public async Task InitializeAsync()
    {
        _database = await SharedPostgres.CreateMigratedDatabaseAsync("api_integration");
        var port = FreeLoopbackPort();
        ApiBaseUrl = $"http://localhost:{port}";

        _factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting($"ConnectionStrings:{ServiceNames.PostgreSql}", _database.AppConnectionString);
            builder.UseSetting($"ConnectionStrings:{ServiceNames.PostgreSql}-migrator", _database.MigratorConnectionString);
            builder.UseSetting("INSTANCE_KEY", InstanceKey);
            builder.UseSetting("BASE_DOMAIN", $"localhost:{port}");
            builder.UseSetting("DemoService:Enabled", "false");
        });
        _factory.UseKestrel(options => options.ListenLocalhost(port));
        _factory.StartServer();

        ApiClient = CreateHttpClient(ServiceNames.NocturneApi);
        using var probe = await ApiClient.GetAsync("/alive");
        if (!probe.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The API at {ApiBaseUrl} answered /alive with {(int)probe.StatusCode}: {probe.Headers} "
                + await probe.Content.ReadAsStringAsync());
        }
    }

    public async Task DisposeAsync()
    {
        ApiClient?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>
    /// The connection string for <paramref name="resourceName"/>. Tests seed and inspect rows
    /// directly, across tenants, so they get the bootstrap superuser.
    /// </summary>
    public Task<string?> GetConnectionStringAsync(string resourceName) =>
        Task.FromResult<string?>(resourceName == ServiceNames.PostgreSql ? _database?.SuperuserConnectionString : null);

    /// <summary>Truncates the data tables, for a clean slate between tests.</summary>
    public async Task CleanupDatabaseAsync()
    {
        if (_database is null)
            return;

        await using var conn = new NpgsqlConnection(_database.SuperuserConnectionString);
        await conn.OpenAsync();

        // Refuse a stale list outright: a table dropped from the schema otherwise fails every
        // test with the same error, far from its cause.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT t FROM unnest(@tables) AS t WHERE to_regclass('public.' || t) IS NULL";
            check.Parameters.AddWithValue("tables", DataTables);
            var missing = new List<string>();
            await using (var reader = await check.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    missing.Add(reader.GetString(0));
            }

            if (missing.Count > 0)
                throw new InvalidOperationException($"{nameof(DataTables)} names tables the schema no longer has: {string.Join(", ", missing)}");
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE TABLE {string.Join(", ", DataTables)} CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The tables <see cref="CleanupDatabaseAsync"/> empties between tests.</summary>
    private static readonly string[] DataTables =
    [
        "alert_deliveries",
        "alert_instances",
        "alert_excursions",
        "alert_invites",
        "alert_rule_channels",
        "alert_condition_timers",
        "alert_tracker_state",
        "alert_rules",
        "dnd_windows",
        "tenant_alert_settings",
        "tracker_instances",
        "tracker_presets",
        "tracker_definitions",
        "in_app_notifications",
        "settings",
        "foods",
        // The v4 records the legacy entries, treatments and profile endpoints decompose into.
        "sensor_glucose",
        "meter_glucose",
        "calibrations",
        "bg_checks",
        "boluses",
        "bolus_calculations",
        "carb_intakes",
        "temp_basals",
        "basal_injections",
        "notes",
        "device_events",
        "state_spans",
        "linked_records",
        "therapy_settings",
        "basal_schedules",
        "carb_ratio_schedules",
        "sensitivity_schedules",
        "target_range_schedules",
        "aps_snapshots",
        "pump_snapshots",
        "uploader_snapshots",
        "device_status_extras",
        "oauth_refresh_tokens",
        "oauth_authorization_codes",
        "oauth_device_codes",
        "oauth_grants",
        "oauth_clients",
        "auth_audit_log",
    ];

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
