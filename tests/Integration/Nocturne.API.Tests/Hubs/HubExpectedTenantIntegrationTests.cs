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

    private HubConnection DataHub(Guid expectedTenant)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ApiClient.BaseAddress!, "hubs/data"), options =>
            {
                options.Headers["X-Forwarded-Host"] = Fixture.TenantHost;
                options.Headers[TenantAwareHub.ExpectedTenantHeader] = expectedTenant.ToString();
            })
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .Build();
        HubConnections.Add(connection);
        return connection;
    }

    private static Task<JsonElement> AuthorizeAsBridgeAsync(HubConnection connection) =>
        connection.InvokeAsync<JsonElement>("Authorize", new
        {
            client = "websocket-bridge",
            secret = HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey),
        });

    [Fact]
    public async Task A_connection_expecting_its_hosts_tenant_is_admitted()
    {
        var connection = DataHub(_tenantId);
        await connection.StartAsync();

        (await AuthorizeAsBridgeAsync(connection)).GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_connection_expecting_another_tenant_is_closed_before_it_can_authorize()
    {
        var connection = DataHub(Guid.NewGuid());
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync();

        (await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().BeSameAs(closed.Task, "the host resolves to a tenant the connection did not expect");
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }
}
