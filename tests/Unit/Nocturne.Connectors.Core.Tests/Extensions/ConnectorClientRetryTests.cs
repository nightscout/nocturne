using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// A retry handler sends a request again only when its method is safe or its builder marked it
/// repeatable. A write that met a failure may still have landed, so a second send can store it twice.
/// </summary>
public class ConnectorClientRetryTests
{
    private const string ClientName = "retry-test-connector-client";
    private const string Url = "https://203.0.113.10/api/entries";

    private sealed class FailingHandler : HttpMessageHandler
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

    private static async Task<int> RequestsSentAsync(HttpRequestMessage request)
    {
        var time = new FakeTimeProvider();
        var handler = new FailingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services
            .AddHttpClient(ClientName)
            .ConfigureConnectorClient(null, addResilience: true)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using (request)
        {
            var work = client.SendAsync(request);
            await Eventually.UntilAsync(
                "the request to complete",
                () => work.IsCompleted,
                step: TimeSpan.FromMilliseconds(1),
                eachStep: () => time.Advance(TimeSpan.FromSeconds(5)));

            using var response = await work;
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        return handler.Requests;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task An_unmarked_write_is_sent_once(string method)
    {
        var requests = await RequestsSentAsync(
            new HttpRequestMessage(new HttpMethod(method), Url) { Content = new StringContent("{}") });

        requests.Should().Be(1);
    }

    [Fact]
    public async Task A_marked_post_is_retried()
    {
        var requests = await RequestsSentAsync(
            new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent("{}") }
                .MarkRepeatable());

        requests.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task A_safe_method_is_retried(string method)
    {
        var requests = await RequestsSentAsync(new HttpRequestMessage(new HttpMethod(method), Url));

        requests.Should().BeGreaterThan(1);
    }
}
