using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Webhooks;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Webhooks;

/// <summary>
/// <see cref="OutboundDestination"/> can only vet the URL it is handed, so the client that
/// sends the request must not follow a redirect off that URL — otherwise the allowlist is a
/// pre-flight check the transport walks straight past into the deployment's own network.
/// </summary>
public class WebhookRequestSenderRedirectTests
{
    [Fact]
    public void WebhookHttpClient_DoesNotFollowRedirects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlertingAndMonitoring(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(WebhookRequestSender.HttpClientName);

        // Replay the registered builder actions to see the handler the factory would use.
        var builder = new HandlerBuilder();
        foreach (var configure in options.HttpMessageHandlerBuilderActions)
        {
            configure(builder);
        }

        builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse(
                "a 307 off an allowed host would otherwise reach an internal address");
    }

    private sealed class HandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
