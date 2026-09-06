using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Services.Migration;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.API.Tests.Net;

/// <summary>
/// The HTTP clients that fetch a URL somebody signed in supplied. Each one sends from inside the
/// deployment's network and reports the outcome back — a status code, a discovery document, an
/// ingested body — so each is a request-forgery primitive and has to be pinned to an address that
/// passed a policy.
/// </summary>
/// <remarks>
/// Registration is asserted rather than behaviour, because the failure being guarded against is a
/// call site reaching for <c>CreateClient()</c> with no name: the migration test endpoint and the
/// unsaved-provider test button both did, which gave them the default client — no guard, no pin,
/// redirects followed, and .NET's redirect handling strips <c>Authorization</c> but not the
/// <c>api-secret</c> the migration sends.
/// </remarks>
public class OutboundSinkGuardTests
{
    [Fact]
    public void TheNightscoutMigrationClient_IsGuardedAndPinnedForAConnectorTarget()
    {
        var client = Registered(services => services.AddMigrationServices(),
            MigrationJobService.HttpClientName);

        client.AdditionalHandlers.Should().ContainItemsAssignableTo<LinkLocalGuardHandler>(
            "a Nightscout that answers 3xx is ordinary, and the hop has to be re-checked and the " +
            "tenant's api-secret dropped if it crosses origin");
        client.Pin.Should().NotBeNull();
        client.Pin!.Policy.Should().Be(OutboundAddressPolicy.NotLinkLocal,
            "the Nightscout being migrated from is routinely on the LAN or the same Docker network");
        client.FollowsRedirects.Should().BeFalse(
            "the guard follows them instead, so every hop is checked");
    }

    [Fact]
    public void TheWebhookClient_IsPinnedToPubliclyRoutableAddresses()
    {
        var client = Registered(
            services => services.AddAlertingAndMonitoring(EmptyConfiguration()),
            WebhookRequestSender.HttpClientName);

        client.Pin.Should().NotBeNull();
        client.Pin!.Policy.Should().Be(OutboundAddressPolicy.PubliclyRoutable,
            "a webhook notifies a third-party service on the internet; nothing private is a " +
            "legitimate target");
        client.FollowsRedirects.Should().BeFalse();
    }

    [Fact]
    public void TheOidcProviderClient_IsPinned()
    {
        var client = Registered(
            services => services.AddAuthenticationAndIdentity(EmptyConfiguration()),
            "OidcProvider");

        client.Pin.Should().NotBeNull(
            "the issuer URL is tenant configuration, and this client fetches discovery, exchanges " +
            "tokens and reports the result of the unsaved-provider test to its caller");
        client.Pin!.Policy.Should().Be(OutboundAddressPolicy.NotLinkLocal,
            "a self-hosted deployment legitimately runs its IdP on the same Docker network, so " +
            "requiring public routability would break the login path and not just the test button");
    }

    [Fact]
    public async Task TheMigrationConnectionTest_AsksForTheGuardedClientByName()
    {
        // Registering a guarded client changes nothing if the call site keeps reaching for the
        // unnamed default, which is what this endpoint did.
        var factory = new RecordingClientFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(factory);

        using var provider = services.BuildServiceProvider();
        var migration = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            provider,
            EmptyConfiguration());

        await migration.TestConnectionAsync(new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = "https://someones-nightscout.example",
            NightscoutApiSecret = "the-tenant-secret",
        });

        factory.Requested.Should().Equal(MigrationJobService.HttpClientName);
    }

    [Fact]
    public async Task TheUnsavedOidcProviderTest_AsksForTheGuardedClientByName()
    {
        var factory = new RecordingClientFactory();
        var controller = new OidcProviderAdminController(
            Mock.Of<IOidcProviderService>(), dbContext: null!, factory);

        await controller.TestUnsaved(new TestProviderRequest("https://someones-idp.example"));

        factory.Requested.Should().Equal("OidcProvider");
    }

    /// <summary>
    /// Answers everything with an empty 200, recording the name each client was asked for. The
    /// unnamed default arrives as <see cref="Options.DefaultName"/>, so a call site that never named
    /// a client is distinguishable from one that did.
    /// </summary>
    private sealed class RecordingClientFactory : IHttpClientFactory
    {
        public List<string> Requested { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Requested.Add(name);
            return new HttpClient(new EmptyOkHandler());
        }

        private sealed class EmptyOkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed record ClientPipeline(
        IList<DelegatingHandler> AdditionalHandlers,
        HttpMessageHandler Primary)
    {
        public PinnedConnector? Pin =>
            (Primary as SocketsHttpHandler)?.ConnectCallback?.Target as PinnedConnector;

        public bool FollowsRedirects => Primary switch
        {
            SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
            HttpClientHandler legacy => legacy.AllowAutoRedirect,
            // Unreadable counts as following: "redirects are off" and "the test could not tell"
            // must not look alike.
            _ => true,
        };
    }

    private static ClientPipeline Registered(
        Action<IServiceCollection> register, string clientName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        register(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(clientName);

        options.HttpMessageHandlerBuilderActions.Should().NotBeEmpty(
            "'{0}' has to be a registered named client; an unregistered name yields default " +
            "options and every assertion below would be about a client nobody built", clientName);

        var builder = new HandlerBuilder(provider) { Name = clientName };
        foreach (var configure in options.HttpMessageHandlerBuilderActions)
            configure(builder);

        return new ClientPipeline(builder.AdditionalHandlers, builder.PrimaryHandler);
    }

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    private sealed class HandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        // The framework's own default, which allows redirects — so a client that configures no
        // primary handler fails the redirect assertions rather than being waved through.
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];
        public override IServiceProvider Services { get; } = services;
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
