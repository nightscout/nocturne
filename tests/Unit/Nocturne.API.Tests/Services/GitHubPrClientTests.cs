using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Services;

public class GitHubPrClientTests
{
    private static (GitHubPrClient Client, HttpClient Http, List<string> Bodies) CreateClient()
    {
        var bodies = new List<string>();
        var handler = new CapturingHandler(bodies);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(http);

        return (new GitHubPrClient(factory.Object, NullLogger<GitHubPrClient>.Instance), http, bodies);
    }

    [Fact]
    public async Task CommitFileAsync_Omits_Sha_When_Creating_A_File()
    {
        var (prClient, http, bodies) = CreateClient();

        await prClient.CommitFileAsync(
            http, "nightscout", "nocturne", "src/Web/packages/portal/src/content/blog/new.svx",
            "content/new-abc", fileSha: null, "body", "content: add new", CancellationToken.None);

        var payload = JsonDocument.Parse(bodies.Single()).RootElement;
        payload.TryGetProperty("sha", out _).Should().BeFalse();
        payload.GetProperty("branch").GetString().Should().Be("content/new-abc");
    }

    [Fact]
    public async Task CommitFileAsync_Sends_Sha_When_Updating_A_File()
    {
        var (prClient, http, bodies) = CreateClient();

        await prClient.CommitFileAsync(
            http, "nightscout", "nocturne", "src/Web/locales/fr.po",
            "translations/fr-abc", fileSha: "deadbeef", "body", "chore(i18n): fr", CancellationToken.None);

        var payload = JsonDocument.Parse(bodies.Single()).RootElement;
        payload.GetProperty("sha").GetString().Should().Be("deadbeef");
    }

    private sealed class CapturingHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}
