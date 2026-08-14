using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
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

        var result = await verifier.VerifyAsync(BuildConfigurationJson(), BuildSecrets(), CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.Success.Should().BeTrue();
        handler.SignInRequests.Should().Be(1);
    }

    [Fact]
    public async Task VerifyAsync_RejectedCredentials_FailsWithoutEchoingSecrets()
    {
        var handler = new SignInHandler(acceptCredentials: false);
        var verifier = BuildVerifier(handler);

        var result = await verifier.VerifyAsync(BuildConfigurationJson(), BuildSecrets(), CancellationToken.None);

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
            BuildConfigurationJson(), new Dictionary<string, string>(), CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.Message.Should().NotBeNullOrEmpty();
        handler.SignInRequests.Should().Be(0, "required-field validation must run before any network call");
    }

    // ── Test infrastructure ─────────────────────────────────────────────

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
    private static GlookoCredentialVerifier BuildVerifier(SignInHandler handler)
    {
        var tokenProvider = new GlookoAuthTokenProvider(
            new HttpClient(handler),
            new Mock<IConnectorTokenCache>(MockBehavior.Strict).Object,
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            new UnresolvedTenantAccessor(),
            NullLogger<GlookoAuthTokenProvider>.Instance);

        return new GlookoCredentialVerifier(tokenProvider);
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
    /// </summary>
    private sealed class SignInHandler(bool acceptCredentials) : HttpMessageHandler
    {
        public int SignInRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.Contains("/users/sign_in", StringComparison.OrdinalIgnoreCase))
            {
                SignInRequests++;

                if (!acceptCredentials)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(
                            "{\"error\":\"Invalid email or password.\"}", Encoding.UTF8, "application/json"),
                    });
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"userLogin\":{\"glookoCode\":\"eu-west-1-blue-duke-4165\"}}",
                        Encoding.UTF8, "application/json"),
                };
                response.Headers.Add(
                    "Set-Cookie", $"{GlookoConstants.SessionCookieName}=sess; Path=/; HttpOnly");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
