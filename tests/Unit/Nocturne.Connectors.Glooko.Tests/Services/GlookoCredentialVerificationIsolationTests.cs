using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Verification runs through the installed registrations, not a hand-built provider, so these
/// tests see the same HTTP clients, resilience pipelines and circuit breakers production does.
/// </summary>
public class GlookoCredentialVerificationIsolationTests
{
    /// <summary>
    /// The sync client's circuit breaker is shared by every tenant. A member who keeps testing
    /// credentials while Glooko is failing must not open it for everyone else's sync.
    /// </summary>
    [Fact]
    public async Task RepeatedFailedVerifications_LeaveTheSyncCircuitBreakerClosed()
    {
        var glooko = new FakeGlooko { Status = HttpStatusCode.ServiceUnavailable };
        await using var provider = BuildProvider(glooko);

        for (var i = 0; i < 10; i++)
        {
            await using var scope = provider.CreateAsyncScope();
            var verifier = scope.ServiceProvider.GetServices<IConnectorCredentialVerifier>().Single();

            var result = await verifier.VerifyAsync(
                JsonDocument.Parse("""{"email":"user@example.com","server":"EU","useV3Api":false}"""),
                new Dictionary<string, string> { ["password"] = "wrong" },
                [],
                CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        glooko.Requests.Should().Be(10, "verification makes one attempt and has no retry pipeline");

        glooko.Status = HttpStatusCode.OK;
        var sync = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GlookoAuthTokenProvider));

        using var response = await sync.GetAsync("https://eu.api.glooko.com/api/v2/users/sign_in");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the sync breaker never saw the verification failures");
    }

    private static ServiceProvider BuildProvider(FakeGlooko glooko)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IRetryDelayStrategy>().Object);
        services.AddSingleton(new Mock<ITenantAccessor>().Object);
        services.AddSingleton(new Mock<IConnectorConfigurationService>(MockBehavior.Strict).Object);

        new GlookoConnectorInstaller().Install(
            services,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Connectors:Glooko:Enabled"] = "true" })
                .Build());

        foreach (var name in new[]
                 {
                     nameof(GlookoAuthTokenProvider),
                     CredentialVerificationTokenProvider<GlookoAuthTokenProvider>.ClientName,
                 })
        {
            // The link-local guard resolves the real Glooko host names; the fake answers in its place.
            services.AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(() => glooko)
                .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
                    handlers.Remove(handlers.Single(h => h is LinkLocalGuardHandler)));
        }

        return services.BuildServiceProvider();
    }

    private sealed class FakeGlooko : HttpMessageHandler
    {
        private int _requests;

        public HttpStatusCode Status { get; set; }

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(Status));
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
