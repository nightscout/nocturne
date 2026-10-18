using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthSessionStoreTests
{
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

        await store.ClearAsync(removeAccount, Guid.NewGuid(), default);

        secrets.Keys.Should().BeEquivalentTo(removeAccount ? [] : new[] { "accountKey" });
        savedEnabled.Should().BeFalse();
    }

    private bool? savedEnabled;

    private GoogleHealthSessionStore Store(
        out Mock<IConnectorConfigurationService> configurations, HttpMessageHandler? handler = null)
    {
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
        tenant.SetupGet(accessor => accessor.TenantId).Returns(Guid.NewGuid());
        var oauth = new GoogleHealthAuthTokenProvider(
            new HttpClient(handler ?? new ThrowingHandler()),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
            tenant.Object,
            TestPlatform.Options(),
            NullLogger<GoogleHealthAuthTokenProvider>.Instance);
        return new GoogleHealthSessionStore(configurations.Object, oauth, TestPlatform.Options());
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.OK)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    public async Task Clearing_a_former_clients_session_revokes_its_token_best_effort(System.Net.HttpStatusCode response)
    {
        var revoked = new List<string>();
        var store = Store(out _, new RecordingHandler(request =>
        {
            revoked.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(response) { Content = new StringContent("{}") };
        }));

        await store.ClearAsync(removeAccount: false, Guid.NewGuid(), default, revoke: true);

        revoked.Should().ContainSingle().Which.Should().Contain("token=refresh");
        secrets.Should().NotContainKey("refreshToken");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Google must not be called.");
    }
}
