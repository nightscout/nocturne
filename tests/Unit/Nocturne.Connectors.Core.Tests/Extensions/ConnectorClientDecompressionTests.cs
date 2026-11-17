using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// The encodings a connector's client decodes. See <see cref="HttpClientExtensions"/> for why Brotli
/// is not one of them.
/// </summary>
public class ConnectorClientDecompressionTests
{
    private const string ClientName = "test-connector-client";

    [Fact]
    public void ConnectorClient_DoesNotDecodeBrotli()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(ClientName).ConfigureConnectorClient("https://api.example.com");

        using var provider = services.BuildServiceProvider();
        HttpMessageHandler? handler =
            provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler;

        var decompression = handler.Should().BeOfType<SocketsHttpHandler>().Subject.AutomaticDecompression;
        decompression.Should().HaveFlag(DecompressionMethods.GZip);
        decompression.Should().NotHaveFlag(DecompressionMethods.Brotli);
    }
}
