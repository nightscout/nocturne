using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nocturne.API.Hubs;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Hubs;

/// <summary>
/// A hub connection that names the tenant it expects in <see cref="TenantAwareHub.ExpectedTenantHeader"/>
/// is refused when its host resolves to any other tenant, as it does once the slug it was opened for
/// is deleted and created again.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class HubExpectedTenantIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;

    public HubExpectedTenantIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
    }

    private HubConnection Hub(string path, Guid? expectedTenant, bool asBridgeService = false)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ApiClient.BaseAddress!, path), options =>
            {
                options.Headers["X-Forwarded-Host"] = Fixture.TenantHost;
                if (expectedTenant is { } expected)
                    options.Headers[TenantAwareHub.ExpectedTenantHeader] = expected.ToString();
                if (asBridgeService)
                {
                    options.Headers["X-Instance-Key"] = HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey);
                    options.Headers["X-Instance-Service"] = "nocturne-bridge";
                }
            })
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .Build();
        HubConnections.Add(connection);
        return connection;
    }

    private static Task ClosedAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    private static async Task ShouldCloseAsync(Task closed) =>
        (await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().BeSameAs(closed, "the host resolves to a tenant the connection did not expect");

    private static Task<JsonElement> AuthorizeAsBridgeAsync(HubConnection connection) =>
        connection.InvokeAsync<JsonElement>("Authorize", new
        {
            client = "websocket-bridge",
            secret = HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey),
        });

    [Fact]
    public async Task A_connection_expecting_its_hosts_tenant_is_admitted()
    {
        var connection = Hub("hubs/data", _tenantId);
        await connection.StartAsync();

        (await AuthorizeAsBridgeAsync(connection)).GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_connection_that_names_no_tenant_is_admitted()
    {
        var connection = Hub("hubs/data", null);
        await connection.StartAsync();

        (await AuthorizeAsBridgeAsync(connection)).GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_data_hub_connection_expecting_another_tenant_can_neither_authorize_nor_subscribe()
    {
        var connection = Hub("hubs/data", Guid.NewGuid());
        var closed = ClosedAsync(connection);
        await connection.StartAsync();

        await FluentActions.Awaiting(() => AuthorizeAsBridgeAsync(connection)).Should().ThrowAsync<Exception>();
        await FluentActions.Awaiting(() => connection.InvokeAsync<JsonElement>("Subscribe", new { }))
            .Should().ThrowAsync<Exception>();
        await ShouldCloseAsync(closed);
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task A_config_hub_connection_expecting_its_hosts_tenant_subscribes()
    {
        var connection = Hub("hubs/config", _tenantId, asBridgeService: true);
        await connection.StartAsync();

        await connection.InvokeAsync("SubscribeAll");
        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task A_config_hub_connection_expecting_another_tenant_cannot_subscribe()
    {
        var connection = Hub("hubs/config", Guid.NewGuid(), asBridgeService: true);
        var closed = ClosedAsync(connection);
        await connection.StartAsync();

        await FluentActions.Awaiting(() => connection.InvokeAsync("SubscribeAll")).Should().ThrowAsync<Exception>();
        await ShouldCloseAsync(closed);
    }
}
