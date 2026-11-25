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
                .MarkRepeatable(maxAttempts: 4));

        requests.Should().BeGreaterThan(1);
    }

    /// <summary>
    /// A sign-in carries the tenant's attempt budget, so the handler sends it no more often than
    /// that, however many retries the handler would otherwise spend.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_marked_post_is_sent_no_more_than_its_attempt_budget(int maxAttempts)
    {
        var requests = await RequestsSentAsync(
            new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent("{}") }
                .MarkRepeatable(maxAttempts));

        requests.Should().Be(maxAttempts);
    }

    [Fact]
    public async Task A_budget_above_the_handlers_ceiling_is_spent_to_the_ceiling()
    {
        var requests = await RequestsSentAsync(
            new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent("{}") }
                .MarkRepeatable(maxAttempts: 10));

        requests.Should().Be(4, "the handler's own ceiling is one send and three retries");
    }

    [Fact]
    public async Task A_request_that_went_through_the_handler_says_so()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);

        await RequestsSentAsync(request);

        RepeatableRequest.PassedRetryHandler(request).Should().BeTrue();
    }

    [Fact]
    public async Task A_request_on_a_client_without_the_handler_does_not()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddHttpClient(ClientName)
            .ConfigureConnectorClient(null)
            .ConfigurePrimaryHttpMessageHandler(() => new FailingHandler());
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);

        using var response = await client.SendAsync(request);

        RepeatableRequest.PassedRetryHandler(request).Should().BeFalse();
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
