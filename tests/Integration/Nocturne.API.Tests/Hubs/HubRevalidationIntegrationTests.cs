using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.API.Hubs;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Hubs;

/// <summary>
/// A hub connection stays open while the server-side records behind its credential stand, however
/// short-lived the access token it connected with, and <see cref="HubConnectionRevalidator"/>
/// closes it once they do not: a revoked grant, a removed member or a deactivated tenant. Covers a credential presented
/// on the upgrade request and one presented in-band.
/// </summary>
[Trait("Category", "Integration")]
public class HubRevalidationIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;

    public HubRevalidationIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenAsync();
        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Revalidation User");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }

    private async Task<(Guid GrantId, string Secret)> SeedSecretAsync()
    {
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        await using var conn = await OpenAsync();
        return (await AuthTestHelpers.SeedApiSecretGrantAsync(conn, _tenantId, _subjectId, secret), secret);
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var conn = await OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", _tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("id", id);
            (await cmd.ExecuteNonQueryAsync()).Should().BeGreaterThan(0);
        }
    }

    private Task RevokeAsync(Guid grantId) =>
        ExecuteAsync("UPDATE oauth_grants SET revoked_at = now() WHERE id = @id;", grantId);

    private Task RemoveMemberAsync() =>
        ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", _subjectId);

    /// <summary>An OAuth access token for the seeded member, minted from <paramref name="grantId"/>.</summary>
    private string AccessToken(Guid grantId, TimeSpan lifetime)
    {
        using var scope = Fixture.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IJwtService>().GenerateAccessToken(
            new SubjectInfo { Id = _subjectId, Name = "Revalidation User" },
            permissions: [],
            roles: [],
            scopes: [Scope.GlucoseRead, Scope.AlertsRead],
            clientId: "revalidation-test",
            tenantId: _tenantId,
            lifetime: lifetime,
            grantId: grantId);
    }

    private HubConnection Hub(string path, Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? configure = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ApiClient.BaseAddress!, path), options =>
            {
                options.Headers["X-Forwarded-Host"] = Fixture.TenantHost;
                configure?.Invoke(options);
            })
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .Build();
        HubConnections.Add(connection);
        return connection;
    }

    private async Task<HubConnection> AlertHubWithSecretAsync(string secret)
    {
        var connection = Hub("hubs/alerts", options => options.Headers["api-secret"] = secret);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe");
        return connection;
    }

    /// <summary>The alert hub, with the access token presented on the upgrade request.</summary>
    private async Task<HubConnection> AlertHubWithTokenAsync(string token)
    {
        var connection = Hub("hubs/alerts", options => options.AccessTokenProvider = () => Task.FromResult<string?>(token));
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe");
        return connection;
    }

    /// <summary>The data hub, with the access token presented in-band.</summary>
    private async Task<HubConnection> DataHubWithTokenAsync(string token)
    {
        var connection = Hub("hubs/data");
        await connection.StartAsync();
        var result = await connection.InvokeAsync<JsonElement>("Authorize", new { client = "test", token });
        result.GetProperty("success").GetBoolean().Should().BeTrue();
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

    private async Task RevalidateAsync() =>
        await Fixture.Services.GetRequiredService<HubConnectionRevalidator>().RevalidateAsync(CancellationToken.None);

    private static async Task ShouldCloseAsync(Task closed, string because) =>
        (await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(closed, because);

    [Fact]
    public async Task A_connection_whose_api_secret_is_revoked_is_closed_and_a_live_one_stays_open()
    {
        var (revokedGrant, revokedSecret) = await SeedSecretAsync();
        var (_, liveSecret) = await SeedSecretAsync();
        var revoked = await AlertHubWithSecretAsync(revokedSecret);
        var live = await AlertHubWithSecretAsync(liveSecret);
        var revokedClosed = ClosedAsync(revoked);

        await RevokeAsync(revokedGrant);
        await RevalidateAsync();

        await ShouldCloseAsync(revokedClosed, "the revoked credential's connection must be closed");
        live.State.Should().Be(HubConnectionState.Connected);
        await live.InvokeAsync("Subscribe");
    }

    [Fact]
    public async Task A_token_connection_outlives_its_access_token_and_closes_when_its_grant_is_revoked()
    {
        var (grantId, _) = await SeedSecretAsync();
        var token = AccessToken(grantId, TimeSpan.FromSeconds(1));
        var upgrade = await AlertHubWithTokenAsync(token);
        var inBand = await DataHubWithTokenAsync(token);
        var upgradeClosed = ClosedAsync(upgrade);
        var inBandClosed = ClosedAsync(inBand);

        // Past the validator's 30-second clock skew, so the token itself no longer authenticates.
        await Task.Delay(TimeSpan.FromSeconds(33));
        using (var probe = AuthTestHelpers.CreateBearerClient(Fixture, token))
        {
            probe.DefaultRequestHeaders.Add("X-Forwarded-Host", Fixture.TenantHost);
            (await probe.GetAsync("/api/v4/alerts/active")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        await RevalidateAsync();

        upgrade.State.Should().Be(HubConnectionState.Connected, "the grant behind the expired token is live");
        inBand.State.Should().Be(HubConnectionState.Connected, "the grant behind the expired token is live");

        await RevokeAsync(grantId);
        await RevalidateAsync();

        await ShouldCloseAsync(upgradeClosed, "the upgrade credential's grant was revoked");
        await ShouldCloseAsync(inBandClosed, "the in-band credential's grant was revoked");
    }

    [Fact]
    public async Task A_token_connection_closes_when_its_member_is_removed()
    {
        var (grantId, _) = await SeedSecretAsync();
        var token = AccessToken(grantId, TimeSpan.FromMinutes(15));
        var upgradeClosed = ClosedAsync(await AlertHubWithTokenAsync(token));
        var inBandClosed = ClosedAsync(await DataHubWithTokenAsync(token));

        await RemoveMemberAsync();
        await RevalidateAsync();

        await ShouldCloseAsync(upgradeClosed, "the upgrade credential's member was removed");
        await ShouldCloseAsync(inBandClosed, "the in-band credential's member was removed");
    }

    [Fact]
    public async Task Every_connection_on_a_deactivated_tenant_closes_the_realtime_bridges_included()
    {
        var (grantId, _) = await SeedSecretAsync();
        var memberClosed = ClosedAsync(await DataHubWithTokenAsync(AccessToken(grantId, TimeSpan.FromMinutes(15))));
        var bridge = Hub("hubs/data");
        await bridge.StartAsync();
        var authorized = await bridge.InvokeAsync<JsonElement>("Authorize", new
        {
            client = "websocket-bridge",
            secret = HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey),
        });
        authorized.GetProperty("success").GetBoolean().Should().BeTrue();
        var bridgeClosed = ClosedAsync(bridge);

        using var scope = Fixture.Services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantService>();
        var tenant = (await tenants.GetByIdAsync(_tenantId))!;
        await tenants.UpdateAsync(_tenantId, tenant.DisplayName, isActive: false);
        try
        {
            await RevalidateAsync();

            await ShouldCloseAsync(memberClosed, "the member's tenant was deactivated");
            await ShouldCloseAsync(bridgeClosed, "the bridge relays a tenant that was deactivated");
        }
        finally
        {
            await tenants.UpdateAsync(_tenantId, tenant.DisplayName, isActive: true);
        }
    }
}
