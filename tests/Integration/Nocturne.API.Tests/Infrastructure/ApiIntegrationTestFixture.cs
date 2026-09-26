using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.DevOnly;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Constants;
using Nocturne.Infrastructure.Data;
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
/// <para>
/// The API serves nothing tenant-scoped on the apex of an instance with no tenant, so the fixture
/// seeds one through the dev-only seed endpoint, as the e2e suite does, and every client it hands
/// out addresses that tenant's host. A test that seeds further tenants addresses them by their own
/// host; <see cref="CleanupDatabaseAsync"/> removes them again.
/// </para>
/// </remarks>
public class ApiIntegrationTestFixture : IAsyncLifetime
{
    /// <summary>The instance key the API runs with.</summary>
    public const string InstanceKey = "test-secret-for-integration-tests";

    /// <summary>The slug of the tenant the fixture seeds.</summary>
    public const string TenantSlug = "integration";

    /// <summary>
    /// The <c>api-secret</c> header value that authenticates as the tenant's owner, which
    /// <see cref="CleanupDatabaseAsync"/> restores as a legacy-secret grant before every test.
    /// </summary>
    public const string ApiSecret = "test-secret-for-integration-tests";

    private WebApplicationFactory<Nocturne.API.Program>? _factory;
    private TestDatabase? _database;

    public string ApiBaseUrl { get; private set; } = string.Empty;

    /// <summary>The seeded tenant.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>The seeded tenant's owner, whom <see cref="ApiSecret"/> authenticates as.</summary>
    public Guid OwnerSubjectId { get; private set; }

    /// <summary>The seeded tenant's host, <c>{slug}.localhost:{port}</c>.</summary>
    public string TenantHost { get; private set; } = string.Empty;

    /// <summary>A client for the API with no credentials.</summary>
    public HttpClient ApiClient { get; private set; } = null!;

    /// <summary>The running API's services.</summary>
    public IServiceProvider Services =>
        _factory?.Services ?? throw new InvalidOperationException("The API has not started.");

    /// <summary>
    /// A new client for <paramref name="resourceName"/>, which must be the API: it is the only
    /// service this fixture runs. It addresses the seeded tenant's host; set
    /// <c>DefaultRequestHeaders.Host</c> to address another.
    /// </summary>
    public HttpClient CreateHttpClient(string resourceName, string? endpointName = null)
    {
        var client = CreateApexClient(resourceName);
        client.DefaultRequestHeaders.Host = TenantHost;
        return client;
    }

    /// <summary>
    /// A new client over <paramref name="handler"/> (to hold cookies or see redirects), set up as
    /// <see cref="CreateHttpClient(string, string?)"/> sets one up.
    /// </summary>
    public HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler { UseCookies: true } cookies)
        {
            cookies.UseCookies = false;
            handler = new SecureCookieHandler(cookies.CookieContainer, CookieOrigin) { InnerHandler = cookies };
        }

        var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };
        client.DefaultRequestHeaders.Host = TenantHost;
        PresentNewClientAddress(client);
        return client;
    }

    /// <summary>
    /// The origin cookies for the seeded tenant are kept under: its host over https, since the API
    /// marks its session cookies <c>Secure</c> and a <see cref="CookieContainer"/> sends those only
    /// to an https URI, while the fixture serves plain http.
    /// </summary>
    public Uri CookieOrigin => new($"https://{TenantHost}");

    /// <summary>
    /// Keeps a handler's cookies against <see cref="CookieOrigin"/> in place of the http URI the
    /// request actually goes to.
    /// </summary>
    private sealed class SecureCookieHandler(CookieContainer cookies, Uri origin) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var header = cookies.GetCookieHeader(origin);
            if (header.Length > 0)
                request.Headers.Add("Cookie", header);

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                    cookies.SetCookies(origin, setCookie);
            }

            return response;
        }
    }

    /// <summary>A new client for the API's apex host, which resolves no tenant.</summary>
    public HttpClient CreateApexClient(string resourceName = ServiceNames.NocturneApi)
    {
        if (resourceName != ServiceNames.NocturneApi)
        {
            throw new ArgumentException($"Only {ServiceNames.NocturneApi} runs in this fixture, not {resourceName}.", nameof(resourceName));
        }

        var client = new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
        PresentNewClientAddress(client);
        return client;
    }

    private int _clientAddressCount;

    /// <summary>
    /// Gives <paramref name="client"/> a client address of its own, signed with the instance key
    /// as the web server signs the address it forwards (<c>ClientRateLimitKey</c>). Every request
    /// otherwise arrives from loopback, one partition of the per-client rate limits, so the suite
    /// as a whole would exhaust a limit no single test comes near.
    /// </summary>
    private void PresentNewClientAddress(HttpClient client)
    {
        var n = Interlocked.Increment(ref _clientAddressCount);
        var address = new IPAddress([10, (byte)(n >> 16), (byte)(n >> 8), (byte)n]).ToString();
        var signature = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(InstanceKey), Encoding.UTF8.GetBytes(address)));

        client.DefaultRequestHeaders.Remove(ServiceNames.Headers.ClientIp);
        client.DefaultRequestHeaders.Remove(ServiceNames.Headers.ClientIpSignature);
        client.DefaultRequestHeaders.Add(ServiceNames.Headers.ClientIp, address);
        client.DefaultRequestHeaders.Add(ServiceNames.Headers.ClientIpSignature, signature);
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
        TenantHost = $"{TenantSlug}.localhost:{port}";

        using var apex = CreateApexClient();
        using var probe = await apex.GetAsync("/alive");
        if (!probe.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The API at {ApiBaseUrl} answered /alive with {(int)probe.StatusCode}: {probe.Headers} "
                + await probe.Content.ReadAsStringAsync());
        }

        using var seed = await apex.PostAsJsonAsync(
            "/api/v4/dev-only/admin/seed-tenant",
            new DevSeedTenantRequest(TenantSlug, "Integration", "integration-owner"));
        if (!seed.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Seeding tenant '{TenantSlug}' answered {(int)seed.StatusCode}: " + await seed.Content.ReadAsStringAsync());
        }

        var seeded = await seed.Content.ReadFromJsonAsync<DevSeedTenantResponse>()
            ?? throw new InvalidOperationException("The seed-tenant response had no body.");
        TenantId = seeded.TenantId;
        OwnerSubjectId = seeded.SubjectId;

        ApiClient = CreateHttpClient(ServiceNames.NocturneApi);
        await CleanupDatabaseAsync();

        // Fail here, once, rather than in every test's setup when the tenant host is unusable.
        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        status.Headers.Add("api-secret", ApiSecret);
        using var statusResponse = await ApiClient.SendAsync(status);
        if (!statusResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The seeded tenant at {TenantHost} answered /api/v1/status with {(int)statusResponse.StatusCode}: "
                + await statusResponse.Content.ReadAsStringAsync());
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

    /// <summary>The runtime role's connection string, which row level security binds.</summary>
    public string AppConnectionString =>
        _database?.AppConnectionString ?? throw new InvalidOperationException("The database has not been created.");

    /// <summary>
    /// A context from the API's own factory (the runtime role, with the interceptor that carries
    /// the tenant to row level security) pinned to <paramref name="tenantId"/>, for seeding rows
    /// through the entities.
    /// </summary>
    public NocturneDbContext CreateDbContext(Guid tenantId)
    {
        var context = Services.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
        context.TenantId = tenantId;
        return context;
    }

    /// <summary>
    /// Truncates the data tables and removes every tenant but the seeded one, for a clean slate
    /// between tests, then restores the <see cref="ApiSecret"/> grant.
    /// </summary>
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

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"TRUNCATE TABLE {string.Join(", ", DataTables)} CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }

        var removedSlugs = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM tenants WHERE id <> @id RETURNING slug";
            cmd.Parameters.AddWithValue("id", TenantId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                removedSlugs.Add(reader.GetString(0));
        }

        // Back to the seeded tenant as the seed left it: active, its owner and Public its only
        // members, the owner holding a passkey. Tests deactivate it, delete passkeys and add
        // members without one, and any of those leaves every later test facing the setup gate.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tenants SET is_active = true WHERE id = @tenant;
                DELETE FROM tenant_members m USING subjects s
                WHERE m.subject_id = s.id AND m.tenant_id = @tenant
                  AND m.subject_id <> @owner AND NOT s.is_system_subject;
                INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, label, created_at)
                SELECT @passkey, @owner, @credential, @credential, 0, '{}', 'integration fixture', now()
                WHERE NOT EXISTS (SELECT 1 FROM passkey_credentials WHERE subject_id = @owner);
                """;
            cmd.Parameters.AddWithValue("tenant", TenantId);
            cmd.Parameters.AddWithValue("owner", OwnerSubjectId);
            cmd.Parameters.AddWithValue("passkey", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("credential", Guid.NewGuid().ToByteArray());
            await cmd.ExecuteNonQueryAsync();
        }

        // The API caches tenant resolution by slug, and a later test may seed the same slug anew.
        var cache = Services.GetRequiredService<IMemoryCache>();
        foreach (var slug in removedSlugs.Append(TenantSlug))
            TenantResolutionMiddleware.EvictTenant(cache, slug);

        await AuthTestHelpers.SeedApiSecretGrantAsync(conn, TenantId, OwnerSubjectId, ApiSecret);

        // The shared client starts every test as a client the rate limits have not seen.
        if (ApiClient is not null)
            PresentNewClientAddress(ApiClient);
    }

    /// <summary>The tables <see cref="CleanupDatabaseAsync"/> empties between tests.</summary>
    private static string[] DataTables => [.. RecordTables, .. CredentialTables];

    /// <summary>
    /// The tables holding a tenant's records, as opposed to its members and their credentials.
    /// </summary>
    internal static readonly string[] RecordTables =
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
        // A Nightscout import records what it has run, which a second import reads.
        "migration_runs",
        "migration_sources",
    ];

    /// <summary>The tables holding credentials, which the fixture restores its own into.</summary>
    private static readonly string[] CredentialTables =
    [
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
