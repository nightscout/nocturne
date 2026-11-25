using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Base class for the API integration tests: the API in-process on a real port and its own
/// PostgreSQL database, from <see cref="ApiIntegrationTestFixture"/>. A derived class names its
/// shard in <see cref="ApiIntegrationShards"/>.
/// </summary>
[Parity]
public abstract class ApiIntegrationTestBase : IAsyncLifetime
{
    protected const string TestApiSecret = ApiIntegrationTestFixture.ApiSecret;

    protected readonly ApiIntegrationTestFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly List<HubConnection> HubConnections = new();

    /// <summary>
    /// Pre-configured HttpClient for the Nocturne API
    /// </summary>
    protected HttpClient ApiClient => Fixture.ApiClient;

    private HttpClient? _authenticatedClient;

    /// <summary>
    /// A client authenticated as the seeded tenant's owner through <see cref="TestApiSecret"/>, for
    /// tests of data endpoints rather than of authentication: the tenant grants anonymous callers
    /// nothing.
    /// </summary>
    protected HttpClient AuthenticatedClient => _authenticatedClient ??= CreateAuthenticatedClient();

    protected ApiIntegrationTestBase(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
    {
        Fixture = fixture;
        Output = output;
    }

    public virtual async Task InitializeAsync()
    {
        using var _ = TestPerformanceTracker.MeasureTest($"{GetType().Name}.Initialize");

        await Fixture.CleanupDatabaseAsync();
    }

    public virtual async Task DisposeAsync()
    {
        using var _ = TestPerformanceTracker.MeasureTest($"{GetType().Name}.Dispose");

        foreach (var connection in HubConnections)
        {
            if (connection.State == HubConnectionState.Connected)
            {
                await connection.StopAsync();
            }
            await connection.DisposeAsync();
        }
        HubConnections.Clear();
        _authenticatedClient?.Dispose();
    }

    /// <summary>
    /// Creates an HttpClient for the API resource
    /// </summary>
    protected HttpClient CreateHttpClient(string resourceName)
    {
        return Fixture.CreateHttpClient(resourceName);
    }

    /// <summary>
    /// Creates an HttpClient with the test API secret header set
    /// </summary>
    protected HttpClient CreateAuthenticatedClient()
    {
        var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi, "http");
        client.DefaultRequestHeaders.Add("api-secret", TestApiSecret);
        return client;
    }

    /// <summary>
    /// Creates a SignalR connection to the Data Hub
    /// </summary>
    protected async Task<HubConnection> CreateDataHubConnectionAsync()
    {
        var baseAddress =
            ApiClient.BaseAddress
            ?? throw new InvalidOperationException("API client base address is not configured");

        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, $"hubs/{ServiceNames.DataHub}"),
                options => options.Headers["X-Forwarded-Host"] = Fixture.TenantHost
            )
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        HubConnections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Creates a SignalR connection to the Alarm Hub
    /// </summary>
    protected async Task<HubConnection> CreateAlarmHubConnectionAsync()
    {
        var baseAddress =
            ApiClient.BaseAddress
            ?? throw new InvalidOperationException("API client base address is not configured");

        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, $"hubs/alarms"),
                options => options.Headers["X-Forwarded-Host"] = Fixture.TenantHost
            )
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        HubConnections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Creates a SignalR connection to the Notification Hub
    /// </summary>
    protected async Task<HubConnection> CreateNotificationHubConnectionAsync()
    {
        var baseAddress =
            ApiClient.BaseAddress
            ?? throw new InvalidOperationException("API client base address is not configured");

        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, $"hubs/{ServiceNames.NotificationHub}"),
                options => options.Headers["X-Forwarded-Host"] = Fixture.TenantHost
            )
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        HubConnections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Authorizes a SignalR connection with test credentials
    /// </summary>
    protected async Task AuthorizeConnectionAsync(HubConnection connection)
    {
        var authData = new
        {
            client = "test-client",
            // The bridge's in-band credential: the instance key's digest, as HubTokenAuthorizer
            // compares it.
            secret = HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey),
            history = 24,
        };

        try
        {
            await connection.InvokeAsync("Authorize", authData);
        }
        catch (Exception ex)
        {
            Log($"Authorization failed (expected in some tests): {ex.Message}");
        }
    }

    /// <summary>
    /// Subscribes to storage collections on a SignalR connection
    /// </summary>
    protected async Task SubscribeToCollectionsAsync(HubConnection connection, string[] collections)
    {
        var subscribeData = new { collections };

        try
        {
            await connection.InvokeAsync("Subscribe", subscribeData);
        }
        catch (Exception ex)
        {
            Log($"Subscription failed (expected in some tests): {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the connection string for the PostgreSQL database
    /// </summary>
    protected async Task<string?> GetPostgresConnectionStringAsync()
    {
        return await Fixture.GetConnectionStringAsync(ServiceNames.PostgreSql);
    }

    /// <summary>
    /// An open connection to the PostgreSQL database, for the caller to dispose.
    /// </summary>
    protected async Task<NpgsqlConnection> OpenPostgresAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// An authenticated client whose credential is clamped to the last 24 hours of history.
    /// </summary>
    protected async Task<HttpClient> CreateHistoryClampedClientAsync(
        string name = "Clamped Follower",
        Guid? tenantId = null)
    {
        await using var conn = await OpenPostgresAsync();
        var (_, token) = await AuthTestHelpers.SeedHistoryClampedSubjectAsync(
            conn, tenantId ?? Fixture.TenantId, name);
        return AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, token);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the API's services in a scope pinned to the seeded tenant,
    /// as a request on its host would be.
    /// </summary>
    protected async Task<T> WithTenantScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        return await action(scope.ServiceProvider);
    }

    /// <summary>
    /// Call before a v3 history read or a search by <c>srvModified</c> that follows a write; see
    /// <see cref="HistorySettle"/>.
    /// </summary>
    protected Task SettleHistoryAsync() =>
        WithTenantScopeAsync(async sp =>
        {
            await HistorySettle.UntilHorizonPassesAsync(sp);
            return true;
        });

    /// <summary>
    /// Logs a message to the test output
    /// </summary>
    protected void Log(string message)
    {
        Output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }
}
