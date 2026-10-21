using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthSessionStoreTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

    private Dictionary<string, string> secrets = new()
    {
        ["refreshToken"] = "refresh",
        ["grantedScopes"] = "scope-a",
        ["clientId"] = TestPlatform.ClientId,
        ["clientSecret"] = "former-tenant-secret",
        ["accountKey"] = "account"
    };

    [Theory]
    [InlineData("tenant.apps.googleusercontent.com")]
    [InlineData(null)]
    public async Task A_session_issued_to_another_client_needs_a_new_sign_in_without_calling_google(string? sessionClientId)
    {
        var store = Store(out _);
        var configuration = new GoogleHealthConnectorConfiguration
        {
            RefreshToken = "refresh",
            GrantedScopes = "scope-a",
            SessionClientId = sessionClientId
        };

        var error = await Assert.ThrowsAsync<GoogleHealthException>(
            () => store.RefreshAsync(configuration, force: false, default));

        error.Message.Should().Be("client_changed");
        error.RequiresReconnect.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clearing_drops_the_session_its_client_and_a_former_tenant_secret(bool removeAccount)
    {
        var store = Store(out _);

        await store.ClearAsync(removeAccount, Guid.NewGuid(), revoke: false, default);

        secrets.Keys.Should().BeEquivalentTo(removeAccount ? [] : new[] { "accountKey" });
        savedEnabled.Should().BeFalse();
    }

    private bool? savedEnabled;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Mock<IGoogleHealthGrantHolders> holders = new();
    private readonly List<bool> tokenStoredWhenHeld = [];

    private GoogleHealthSessionStore Store(
        out Mock<IConnectorConfigurationService> configurations, HttpMessageHandler? handler = null,
        TimeSpan? revokeTimeout = null)
    {
        holders.Setup(value => value.HoldAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => tokenStoredWhenHeld.Add(secrets.ContainsKey("refreshToken")))
            .Returns(Task.CompletedTask);
        configurations = new Mock<IConnectorConfigurationService>();
        configurations.Setup(service => service.GetSecretsAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<string, string>(secrets));
        configurations.Setup(service => service.SaveSecretsAsync(
                "GoogleHealth", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, string>, string?, CancellationToken>((_, values, _, _) => secrets = values)
            .Returns(Task.CompletedTask);
        configurations.Setup(service => service.GetConfigurationAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "GoogleHealth",
                Configuration = JsonDocument.Parse("""{"enabled":true}""")
            });
        configurations.Setup(service => service.SaveConfigurationAsync(
                "GoogleHealth", It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, JsonDocument, string?, CancellationToken>((_, document, _, _) =>
                savedEnabled = document.RootElement.GetProperty("enabled").GetBoolean())
            .ReturnsAsync(new ConnectorConfigurationResponse());
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(accessor => accessor.IsResolved).Returns(true);
        tenant.SetupGet(accessor => accessor.TenantId).Returns(tenantId);
        var http = new HttpClient(handler ?? new ThrowingHandler());
        _httpClients.Add(http);
        var oauth = new GoogleHealthAuthTokenProvider(
            http,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
            tenant.Object,
            TestPlatform.Options(),
            NullLogger<GoogleHealthAuthTokenProvider>.Instance);
        return new GoogleHealthSessionStore(
            configurations.Object, oauth, holders.Object, tenant.Object, TestPlatform.Options(),
            NullLogger<GoogleHealthSessionStore>.Instance, revokeTimeout);
    }

    [Fact]
    public async Task Clearing_a_former_clients_session_revokes_its_token()
    {
        var revoked = new List<string>();
        var store = Store(out _, new RecordingHandler(async (request, ct) =>
        {
            revoked.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));

        await store.ClearAsync(removeAccount: false, Guid.NewGuid(), revoke: true, default);

        revoked.Should().ContainSingle().Which.Should().Contain("token=refresh");
        secrets.Should().NotContainKey("refreshToken");
    }

    [Fact]
    public async Task Clearing_releases_the_tenants_hold_without_revoking_an_account_another_tenant_holds()
    {
        holders.Setup(value => value.SharingAsync(tenantId, "account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GoogleHealthGrantSharing.Shared);
        var store = Store(out _);

        await store.ClearAsync(removeAccount: false, Guid.NewGuid(), revoke: true, default);

        secrets.Should().NotContainKey("refreshToken");
        holders.Verify(value => value.ReleaseAsync(tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_grant_for_an_unknown_account_is_kept(string? accountKey)
    {
        var store = Store(out _);

        var outcome = await store.RevokeAsync("refresh", accountKey, default);

        outcome.Should().Be(GoogleHealthRevocation.Undetermined);
    }

    [Fact]
    public async Task A_grant_whose_holders_cannot_be_read_is_kept()
    {
        holders.Setup(value => value.SharingAsync(tenantId, "account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GoogleHealthGrantSharing.Undetermined);
        var store = Store(out _);

        (await store.RevokeAsync("refresh", "account", default)).Should().Be(GoogleHealthRevocation.Undetermined);
    }

    [Fact]
    public async Task Clearing_releases_the_hold_before_asking_who_else_holds_the_account()
    {
        var order = new List<string>();
        holders.Setup(value => value.ReleaseAsync(tenantId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("release")).Returns(Task.CompletedTask);
        holders.Setup(value => value.SharingAsync(tenantId, "account", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("sharing")).ReturnsAsync(GoogleHealthGrantSharing.Shared);
        var store = Store(out _);

        await store.ClearAsync(removeAccount: false, Guid.NewGuid(), revoke: true, default);

        order.Should().Equal("release", "sharing");
    }

    public static TheoryData<string?, string?, string?> StoredSessions => new()
    {
        { "refresh", "account", "account" },
        { "refresh", null, null },
        { "refresh", "", null },
        { null, "account", null },
        { " ", "account", null },
    };

    [Theory]
    [MemberData(nameof(StoredSessions))]
    public void Only_a_stored_token_with_its_account_is_held(string? refreshToken, string? accountKey, string? held)
    {
        var stored = new Dictionary<string, string>();
        if (refreshToken is not null) stored["refreshToken"] = refreshToken;
        if (accountKey is not null) stored["accountKey"] = accountKey;

        GoogleHealthSessionStore.HeldAccount(stored).Should().Be(held);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task An_unstored_sign_in_releases_the_hold_only_when_no_token_is_stored(bool tokenStored, int releases)
    {
        if (!tokenStored) secrets.Remove("refreshToken");
        var store = Store(out _);

        await store.ReleaseUnstoredAsync(default);

        holders.Verify(value => value.ReleaseAsync(tenantId, It.IsAny<CancellationToken>()), Times.Exactly(releases));
    }

    [Fact]
    public async Task Storing_a_session_holds_its_account_before_the_token_is_saved()
    {
        secrets.Clear();
        var store = Store(out _);

        await store.StoreAsync(new("refresh", ["scope-a"]), "account", Guid.NewGuid(), default);

        tokenStoredWhenHeld.Should().Equal(false);
        secrets["refreshToken"].Should().Be("refresh");
        holders.Verify(value => value.HoldAsync(tenantId, "account", It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<string> RevokeOutcomes => ["ok", "refused", "unreachable", "hanging"];

    [Theory]
    [MemberData(nameof(RevokeOutcomes))]
    public async Task Revocation_is_best_effort_and_bounded(string outcome)
    {
        var store = Store(out _, new RecordingHandler(async (_, ct) =>
        {
            switch (outcome)
            {
                case "unreachable":
                    throw new HttpRequestException("unreachable");
                case "hanging":
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
            }
            return new HttpResponseMessage(outcome == "ok"
                ? System.Net.HttpStatusCode.OK
                : System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
        }), revokeTimeout: TimeSpan.FromMilliseconds(100));

        var revoke = store.RevokeAsync("refresh", "account", default);

        (await revoke.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(
            outcome == "ok" ? GoogleHealthRevocation.Revoked : GoogleHealthRevocation.Failed);
    }

    [Fact]
    public async Task Revocation_stops_when_the_caller_cancels()
    {
        var store = Store(out _, new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }), revokeTimeout: TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.RevokeAsync("refresh", "account", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, ct);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Google must not be called.");
    }
}
