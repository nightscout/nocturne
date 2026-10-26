using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Platform.Releases;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform.Releases;

public class GitHubReleaseClientTests
{
    [Fact]
    public async Task ReadsTheLatestReleaseAnonymouslyAsSanitizedHtml()
    {
        HttpRequestMessage? sent = null;
        var client = CreateClient(request =>
        {
            sent = request;
            return Json("""
                {
                  "tag_name": "v0.2.8",
                  "name": "Nocturne 0.2.8",
                  "html_url": "https://github.com/nightscout/nocturne/releases/tag/v0.2.8",
                  "published_at": "2026-10-20T10:00:00Z",
                  "draft": false,
                  "prerelease": false,
                  "body_html": "<h2>What's Changed</h2><script>alert(1)</script><a href=\"https://github.com/x\" onclick=\"x()\">PR</a><a href=\"javascript:alert(1)\">bad</a>"
                }
                """);
        });

        var release = await client.GetLatestAsync(CancellationToken.None);

        sent!.RequestUri!.AbsolutePath.Should().Be("/repos/nightscout/nocturne/releases/latest");
        sent.Headers.Authorization.Should().BeNull();
        sent.Headers.Accept.Should().ContainSingle(a => a.MediaType == "application/vnd.github.html+json");

        release.Should().NotBeNull();
        release!.Version.Should().Be("0.2.8");
        release.Name.Should().Be("Nocturne 0.2.8");
        release.NotesHtml.Should().Contain("<h2>What's Changed</h2>");
        release.NotesHtml.Should().NotContain("script").And.NotContain("onclick").And.NotContain("javascript:");
        release.NotesHtml.Should().Contain("target=\"_blank\"").And.Contain("rel=\"noopener noreferrer\"");
    }

    [Fact]
    public async Task AsksForTheRunningVersionByItsTag()
    {
        HttpRequestMessage? sent = null;
        var client = CreateClient(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        ReleaseVersion.TryParse("0.2.7", out var version);

        var release = await client.GetByVersionAsync(version, CancellationToken.None);

        sent!.RequestUri!.AbsolutePath.Should().Be("/repos/nightscout/nocturne/releases/tags/v0.2.7");
        release.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.3.0-rc.1","html_url":"u","prerelease":true}""")]
    [InlineData("""{"tag_name":"v0.3.0","html_url":"u","draft":true}""")]
    [InlineData("""{"tag_name":"alerts-native","html_url":"u"}""")]
    public async Task IgnoresWhatIsNotAPublishedRelease(string body)
    {
        var client = CreateClient(_ => Json(body));

        (await client.GetLatestAsync(CancellationToken.None)).Should().BeNull();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static GitHubReleaseClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(respond), disposeHandler: true));

        return new GitHubReleaseClient(
            factory.Object,
            Options.Create(new ReleaseCheckOptions()),
            NullLogger<GitHubReleaseClient>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
