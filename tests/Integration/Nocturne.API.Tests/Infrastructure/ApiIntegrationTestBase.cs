using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Base class for the API integration tests: the API in-process on a real port and its own
/// PostgreSQL database, from <see cref="ApiIntegrationTestFixture"/>.
/// </summary>
[Collection("ApiIntegration")]
[Parity]
public abstract class ApiIntegrationTestBase : IAsyncLifetime
{
    protected const string TestApiSecret = "test-secret-for-integration-tests";

    protected readonly ApiIntegrationTestFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly List<HubConnection> HubConnections = new();

    /// <summary>
    /// Pre-configured HttpClient for the Nocturne API
    /// </summary>
    protected HttpClient ApiClient => Fixture.ApiClient;

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
                options => { }
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
                options => { }
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
                options => { }
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
            secret = TestApiSecret,
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
    /// Waits for a condition with timeout
    /// </summary>
    protected async Task<bool> WaitForEventAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// Gets the connection string for the PostgreSQL database
    /// </summary>
    protected async Task<string?> GetPostgresConnectionStringAsync()
    {
        return await Fixture.GetConnectionStringAsync(ServiceNames.PostgreSql);
    }

    /// <summary>
    /// Logs a message to the test output
    /// </summary>
    protected void Log(string message)
    {
        Output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }
}
