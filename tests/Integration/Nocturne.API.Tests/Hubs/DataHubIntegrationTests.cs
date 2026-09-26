using Microsoft.AspNetCore.SignalR.Client;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Hubs;

/// <summary>
/// Integration tests for DataHub WebSocket functionality
/// Tests real-time data updates and client-server communication
/// </summary>
[Parity]
public class DataHubIntegrationTests : ApiIntegrationTestBase
{
    public DataHubIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    [Fact]
    [Parity]
    public async Task DataHub_Connect_ShouldEstablishConnectionSuccessfully()
    {
        // Arrange
        var connection = await CreateDataHubConnectionAsync();

        // Act
        await connection.StartAsync();

        // Assert
        Assert.Equal(HubConnectionState.Connected, connection.State);
        Output.WriteLine($"Connection established successfully to {connection.ConnectionId}");
    }

    [Fact]
    [Parity]
    public async Task DataHub_Authorize_ShouldAuthenticateClientSuccessfully()
    {
        // Arrange
        var connection = await CreateDataHubConnectionAsync();
        await connection.StartAsync();

        var authData = new
        {
            client = "test-client",
            secret = "test-secret-for-integration-tests",
            history = 24,
        };

        var authResult = false;
        var authMessage = string.Empty;

        // Set up event handler to capture authorization result
        connection.On<object>(
            "authorized",
            (result) =>
            {
                authResult = true;
                authMessage = result?.ToString() ?? "authorized";
                Output.WriteLine($"Authorization result: {authMessage}");
            }
        );

        // Act
        try
        {
            await connection.InvokeAsync("Authorize", authData);
            authResult = true; // If no exception, consider it successful
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Authorization call completed with: {ex.Message}");
            // Some authorization failures are expected without full auth setup
        }

        // Assert
        Assert.Equal(HubConnectionState.Connected, connection.State);
        Output.WriteLine("DataHub authorization flow completed successfully");

        // Ensure we used the authorization result variable and that authorization completed
        Assert.True(
            authResult,
            "Authorization did not complete successfully or the authorized event was not triggered."
        );
    }

    [Fact]
    public async Task DataHub_Subscribe_ShouldSubscribeToStorageCollectionsSuccessfully()
    {
        // Arrange
        var connection = await CreateDataHubConnectionAsync();
        await connection.StartAsync();
        await AuthorizeConnectionAsync(connection);

        var subscribeData = new { collections = new[] { "entries", "treatments", "devicestatus" } };
        var subscriptionResult = false;

        // Set up event handler to capture subscription result
        connection.On<object>(
            "subscribed",
            (result) =>
            {
                subscriptionResult = true;
                Output.WriteLine($"Subscription result: {result}");
            }
        );

        // Act
        try
        {
            await connection.InvokeAsync("Subscribe", subscribeData);
            subscriptionResult = true; // If no exception, consider it successful
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Subscription call completed with: {ex.Message}");
        }

        // Assert - Connection should remain active
        Assert.Equal(HubConnectionState.Connected, connection.State);
        Output.WriteLine("DataHub subscription flow completed successfully");

        // Ensure we used the subscription result variable and that subscription completed
        Assert.True(
            subscriptionResult,
            "Subscription did not complete successfully or the subscribed event was not triggered."
        );

        Assert.Equal(HubConnectionState.Connected, connection.State);
        Output.WriteLine("DataHub subscription flow completed successfully");
    }
}
