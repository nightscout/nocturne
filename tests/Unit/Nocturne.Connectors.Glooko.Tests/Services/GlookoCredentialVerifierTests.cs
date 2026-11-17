using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Covers credential verification: a live sign-in attempt against Glooko that binds the submitted
/// configuration and secrets to a transient config, never touches the per-tenant token cache, and
/// never surfaces the submitted values in its result.
/// </summary>
public class GlookoCredentialVerifierTests
{
    private const string Email = "user@example.com";
    private const string Password = "hunter2-secret";

    [Fact]
    public async Task VerifyAsync_AcceptedCredentials_ReportsSuccess()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler);

        var result = await verifier.VerifyAsync(BuildConfigurationJson(), BuildSecrets(), [], CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.Success.Should().BeTrue();
        handler.SignInRequests.Should().Be(1);
    }

    [Fact]
    public async Task VerifyAsync_RejectedCredentials_FailsWithoutEchoingSecrets()
    {
        var handler = new SignInHandler(acceptCredentials: false);
        var verifier = BuildVerifier(handler);

        var result = await verifier.VerifyAsync(BuildConfigurationJson(), BuildSecrets(), [], CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.Message.Should().NotBeNullOrEmpty();
        result.Message.Should().NotContain(Password);
        result.Message.Should().NotContain(Email);
    }

    [Fact]
    public async Task VerifyAsync_MissingPassword_FailsWithoutContactingProvider()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler);

        var result = await verifier.VerifyAsync(
            BuildConfigurationJson(), new Dictionary<string, string>(), [], CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.Message.Should().NotBeNullOrEmpty();
        handler.SignInRequests.Should().Be(0, "required-field validation must run before any network call");
    }

    /// <summary>
    /// A retryable provider failure ends the verification rather than entering the sync path's
    /// backoff: ProductionRetryDelayStrategy waits 30 seconds before the second attempt, so
    /// honouring the submitted attempt budget would hold the caller's request for minutes for an
    /// answer they asked for interactively.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_RetryableProviderFailure_TriesOnceAndDoesNotBackOff()
    {
        var handler = new SignInHandler(
            acceptCredentials: false, rejectWith: HttpStatusCode.ServiceUnavailable);
        var retryDelay = new Mock<IRetryDelayStrategy>(MockBehavior.Strict);
        var verifier = BuildVerifier(handler, retryDelay);

        var configuration = JsonDocument.Parse(
            $$"""{"email":"{{Email}}","server":"{{GlookoConstants.RegionEU}}","useV3Api":false,"maxRetryAttempts":10}""");

        var result = await verifier.VerifyAsync(configuration, BuildSecrets(), [], CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.Success.Should().BeFalse();
        handler.SignInRequests.Should().Be(
            1, "a submitted attempt budget must not buy extra sign-ins on the verification path");
        retryDelay.VerifyNoOtherCalls();
    }

    /// <summary>
    /// An operator's <c>CONNECT_GLOOKO_SERVER</c> reaches sync through the registration defaults, and
    /// the settings form never submits a server the stored row does not carry. Verification must
    /// sign in where sync would, or it calls correct credentials wrong.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_ServerNotSubmitted_SignsInAtTheConfiguredDefaultServer()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler, defaults: new GlookoConnectorConfiguration
        {
            Server = GlookoConstants.RegionEU,
            UseV3Api = false,
        });

        var result = await verifier.VerifyAsync(
            JsonDocument.Parse($$"""{"email":"{{Email}}"}"""), BuildSecrets(), [], CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.SignInHosts.Should().Equal("eu.api.glooko.com");
    }

    [Fact]
    public async Task VerifyAsync_StoredPassword_SignsInWithItAndGivesTheSubmittedOutcome()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler, stored: StoredGlooko());

        var result = await verifier.VerifyAsync(null, [], ["password"], CancellationToken.None);

        result.Should().BeEquivalentTo(ConnectorCredentialVerificationResult.Verified());
        handler.SignInPasswords.Should().Equal(Password);
        handler.SignInHosts.Should().Equal("eu.api.glooko.com");
    }

    [Fact]
    public async Task VerifyAsync_StoredPasswordRejected_FailsExactlyAsASubmittedOneWould()
    {
        var submitted = await BuildVerifier(new SignInHandler(acceptCredentials: false))
            .VerifyAsync(BuildConfigurationJson(), BuildSecrets(), [], CancellationToken.None);

        var stored = await BuildVerifier(new SignInHandler(acceptCredentials: false), stored: StoredGlooko())
            .VerifyAsync(null, [], ["password"], CancellationToken.None);

        stored.Should().BeEquivalentTo(submitted);
        stored.Message.Should().NotContain(Password);
    }

    [Fact]
    public async Task VerifyAsync_StoredPasswordWithTheStoredConfigurationRepeated_IsAllowed()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler, stored: StoredGlooko());

        var result = await verifier.VerifyAsync(BuildConfigurationJson(), [], ["password"], CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.SignInPasswords.Should().Equal(Password);
    }

    /// <summary>
    /// A stored password must only go where the tenant's stored configuration sends it, so a
    /// caller cannot name a server and have the stored secret signed in there.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_StoredPasswordWithAnotherServer_IsRefusedBeforeReadingSecrets()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var configService = new Mock<IConnectorConfigurationService>(MockBehavior.Strict);
        configService.Setup(s => s.GetConfigurationAsync("Glooko", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredGlooko().Configuration);
        var verifier = BuildVerifier(handler, configService: configService);

        var verify = () => verifier.VerifyAsync(
            JsonDocument.Parse($$"""{"server":"{{GlookoConstants.RegionUS}}"}"""), [], ["password"],
            CancellationToken.None);

        await verify.Should().ThrowAsync<InvalidVerificationRequestException>();
        handler.SignInRequests.Should().Be(0);
        configService.Verify(s => s.GetSecretsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("server")]
    [InlineData("notAField")]
    public async Task VerifyAsync_StoredKeyThatIsNotASecret_IsRefused(string key)
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler, stored: StoredGlooko());

        var verify = () => verifier.VerifyAsync(null, [], [key], CancellationToken.None);

        await verify.Should().ThrowAsync<InvalidVerificationRequestException>();
        handler.SignInRequests.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_SecretBothSubmittedAndStored_IsRefused()
    {
        var handler = new SignInHandler(acceptCredentials: true);
        var verifier = BuildVerifier(handler, stored: StoredGlooko());

        var verify = () => verifier.VerifyAsync(null, BuildSecrets(), ["password"], CancellationToken.None);

        await verify.Should().ThrowAsync<InvalidVerificationRequestException>();
        handler.SignInRequests.Should().Be(0);
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private sealed record StoredConnector(
        ConnectorConfigurationResponse Configuration, Dictionary<string, string> Secrets);

    private static StoredConnector StoredGlooko() => new(
        new ConnectorConfigurationResponse { ConnectorName = "Glooko", Configuration = BuildConfigurationJson() },
        new Dictionary<string, string> { ["password"] = Password });

    private static JsonDocument BuildConfigurationJson() => JsonDocument.Parse(
        $$"""{"email":"{{Email}}","server":"{{GlookoConstants.RegionEU}}","useV3Api":false}""");

    private static Dictionary<string, string> BuildSecrets() => new()
    {
        ["password"] = Password,
    };

    /// <summary>
    /// The token cache is a strict mock and the tenant accessor reports unresolved: verification
    /// must neither read or write a cached session nor require a tenant context.
    /// </summary>
    private static GlookoCredentialVerifier BuildVerifier(
        SignInHandler handler,
        Mock<IRetryDelayStrategy>? retryDelayStrategy = null,
        GlookoConnectorConfiguration? defaults = null,
        StoredConnector? stored = null,
        Mock<IConnectorConfigurationService>? configService = null)
    {
        var retryDelay = retryDelayStrategy ?? new Mock<IRetryDelayStrategy>();
        retryDelay.Setup(r => r.ApplyRetryDelayAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var tokenProvider = new GlookoAuthTokenProvider(
            new HttpClient(handler),
            new Mock<IConnectorTokenCache>(MockBehavior.Strict).Object,
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            new UnresolvedTenantAccessor(),
            NullLogger<GlookoAuthTokenProvider>.Instance,
            retryDelay.Object);

        if (configService == null)
        {
            configService = new Mock<IConnectorConfigurationService>(MockBehavior.Strict);
            if (stored != null)
            {
                configService.Setup(s => s.GetConfigurationAsync("Glooko", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(stored.Configuration);
                configService.Setup(s => s.GetSecretsAsync("Glooko", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(stored.Secrets);
            }
        }

        return new GlookoCredentialVerifier(
            new CredentialVerificationTokenProvider<GlookoAuthTokenProvider>(tokenProvider),
            new ConnectorRegistration<GlookoConnectorConfiguration>(
                defaults ?? new GlookoConnectorConfiguration(), "Glooko"),
            configService.Object);
    }

    private sealed class UnresolvedTenantAccessor : Nocturne.Core.Contracts.Multitenancy.ITenantAccessor
    {
        public bool IsResolved => false;
        public Guid TenantId => Guid.Empty;
        public Nocturne.Core.Contracts.Multitenancy.TenantContext? Context => null;
        public void SetTenant(Nocturne.Core.Contracts.Multitenancy.TenantContext context) { }
    }

    /// <summary>
    /// Answers the v2 sign-in endpoint: 200 with a session cookie when accepting, 401 otherwise.
    /// Records the host and password of every sign-in.
    /// </summary>
    private sealed class SignInHandler(bool acceptCredentials, HttpStatusCode? rejectWith = null)
        : HttpMessageHandler
    {
        public int SignInRequests => SignInHosts.Count;

        public List<string> SignInHosts { get; } = [];

        public List<string?> SignInPasswords { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.Contains("/users/sign_in", StringComparison.OrdinalIgnoreCase))
            {
                SignInHosts.Add(request.RequestUri!.Host);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                SignInPasswords.Add(body.RootElement.GetProperty("userLogin").GetProperty("password").GetString());

                if (!acceptCredentials)
                {
                    return new HttpResponseMessage(rejectWith ?? HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(
                            "{\"error\":\"Invalid email or password.\"}", Encoding.UTF8, "application/json"),
                    };
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"userLogin\":{\"glookoCode\":\"eu-west-1-blue-duke-4165\"}}",
                        Encoding.UTF8, "application/json"),
                };
                response.Headers.Add(
                    "Set-Cookie", $"{GlookoConstants.SessionCookieName}=sess; Path=/; HttpOnly");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
