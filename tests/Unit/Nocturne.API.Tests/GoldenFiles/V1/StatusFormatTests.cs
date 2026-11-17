using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class StatusFormatTests : GoldenFileTestBase
{
    private const string Badge = "http://img.shields.io/badge/Nightscout-OK-green.";

    public StatusFormatTests(GoldenFileWebAppFactory factory) : base(factory) { }

    private async Task<HttpResponseMessage> Send(string url, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var plain = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        return await plain.SendAsync(request);
    }

    [Theory]
    [InlineData("/api/v1/status.txt", "text/plain", "STATUS OK")]
    [InlineData("/api/v1/status.html", "text/html", "<h1>STATUS OK</h1>")]
    public async Task AnExtensionAnswersItsFixedBody(string url, string contentType, string body)
    {
        var response = await Send(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
        (await response.Content.ReadAsStringAsync()).Should().Be(body);
    }

    [Fact]
    public async Task TheJsExtensionAssignsTheStatusToServerSettings()
    {
        var response = await Send("/api/v1/status.js");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/javascript");
        body.Should().StartWith("this.serverSettings = {").And.EndWith("};");
        body.Should().Contain("\"status\":\"ok\"");
    }

    [Theory]
    [InlineData("/api/v1/status.svg", "svg")]
    [InlineData("/api/v1/status.png", "png")]
    public async Task ABadgeExtensionRedirectsToTheBadge(string url, string extension)
    {
        var response = await Send(url);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be(Badge + extension);
    }

    [Fact]
    public async Task TheJsonExtensionAnswersJsonWhateverTheAccept()
    {
        var response = await Send("/api/v1/status.json", "text/html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().StartWith("{").And.Contain("\"status\":\"ok\"");
    }

    [Theory]
    [InlineData("*/*")]
    [InlineData("text/*")]
    [InlineData("*/*;q=0.8")]
    [InlineData(null)]
    public async Task AWildcardOrMissingAcceptAnswersHtml(string? accept)
    {
        var response = await Send("/api/v1/status", accept);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Be("<h1>STATUS OK</h1>");
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/css")]
    public async Task AnAcceptThatNamesOrMissesHtmlAnswersHtml(string accept)
    {
        var response = await Send("/api/v1/status", accept);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("*/*, application/json")]
    [InlineData("text/*, application/json")]
    [InlineData("application/json, text/plain, */*")]
    [InlineData("*/*;q=0.1, application/json")]
    [InlineData("text/html;q=0.1, application/json")]
    public async Task AnExplicitJsonAcceptAnswersJson(string accept)
    {
        var response = await Send("/api/v1/status", accept);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().StartWith("{").And.Contain("\"status\":\"ok\"");
    }

    [Theory]
    [InlineData("text/plain", "text/plain", "STATUS OK")]
    [InlineData("application/javascript", "application/javascript", null)]
    public async Task AnExplicitAcceptAnswersThatFormat(string accept, string contentType, string? body)
    {
        var response = await Send("/api/v1/status", accept);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
        if (body is not null)
        {
            (await response.Content.ReadAsStringAsync()).Should().Be(body);
        }
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/svg+xml", "svg")]
    public async Task AnImageAcceptRedirectsToTheBadge(string accept, string extension)
    {
        var response = await Send("/api/v1/status", accept);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be(Badge + extension);
    }

    [Fact]
    public async Task AnExcludedFormatFallsToTheNextOfferedOne()
    {
        var response = await Send("/api/v1/status", "text/html;q=0, */*");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be(Badge + "png");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("image/png")]
    [InlineData("text/plain")]
    public async Task EveryNegotiatedAnswerVariesOnAccept(string? accept)
    {
        var response = await Send("/api/v1/status", accept);

        response.Headers.Vary.Should().Contain("Accept");
    }
}
