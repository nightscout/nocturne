using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

/// <summary>
/// A manual test writes to a person's Nightscout once: a failing answer must not be sent again.
/// </summary>
public class ManualTestClientRetryTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task A_post_that_is_answered_with_a_server_error_is_sent_once()
    {
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompatibilityProxyServices(new ConfigurationBuilder().Build());
        services
            .AddHttpClient(CompatibilityProxyServiceExtensions.ManualTestHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(CompatibilityProxyServiceExtensions.ManualTestHttpClientName);
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://ns.example/api/v1/treatments")
            {
                Content = new StringContent("{}"),
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.Requests);
    }
}
