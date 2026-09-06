using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// <see cref="LinkLocalGuardHandler"/> re-checks every redirect hop, which only works if the
/// transport is not following them first. If the primary handler follows a 3xx, the guard never
/// sees the new URI — it inspects the request it was handed — so the check is bypassed by one
/// response header.
/// </summary>
public class ConnectorClientRedirectTests
{
    private const string ClientName = "test-connector-client";

    [Fact]
    public void ConnectorClient_DoesNotLetTheTransportFollowRedirects()
    {
        var builder = BuildChain();

        builder.PrimaryHandler.Should().BeOfType<SocketsHttpHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse(
                "the guard follows redirects itself so it can re-check each hop; a transport " +
                "that follows them first hides the target from it");
    }

    [Fact]
    public void ConnectorClient_InstallsTheLinkLocalGuard()
    {
        var builder = BuildChain();

        builder.AdditionalHandlers.Should().ContainItemsAssignableTo<LinkLocalGuardHandler>(
            "disabling transport redirects is only safe because the guard follows them instead");
    }

    /// <summary>
    /// Replays the registered builder actions to see the handler chain the factory would build.
    /// </summary>
    private static HandlerBuilder BuildChain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(ClientName).ConfigureConnectorClient(null);

        var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(ClientName);

        // AddHttpMessageHandler resolves the handler from the builder's Services.
        var builder = new HandlerBuilder(provider);
        foreach (var configure in options.HttpMessageHandlerBuilderActions)
            configure(builder);

        return builder;
    }

    private sealed class HandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];
        public override IServiceProvider Services { get; } = services;
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
