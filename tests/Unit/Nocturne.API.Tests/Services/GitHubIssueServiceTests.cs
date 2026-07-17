using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Services;

public class GitHubIssueServiceTests
{
    [Fact]
    public void BuildIssueBody_BugTemplate_IncludesStepsAndExpectedActual()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Test bug",
            Description = "Something is broken",
            StepsToReproduce = "1. Open app\n2. Click button",
            ExpectedBehavior = "Should work",
            ActualBehavior = "Crashes",
            DiagnosticInfo = "{\"userAgent\":\"test\"}",
        };

        var body = GitHubIssueService.BuildIssueBody(request, []);

        body.Should().Contain("## Description");
        body.Should().Contain("Something is broken");
        body.Should().Contain("## Steps to Reproduce");
        body.Should().Contain("1. Open app");
        body.Should().Contain("**Expected:** Should work");
        body.Should().Contain("**Actual:** Crashes");
    }

    [Fact]
    public void BuildIssueBody_FeatureTemplate_IncludesDescriptionOnly()
    {
        var request = new CreateIssueRequest
        {
            Template = "feature",
            Title = "New feature",
            Description = "I want dark mode",
            DiagnosticInfo = "{\"userAgent\":\"test\"}",
        };

        var body = GitHubIssueService.BuildIssueBody(request, []);

        body.Should().Contain("## Description");
        body.Should().Contain("I want dark mode");
        body.Should().NotContain("## Steps to Reproduce");
        body.Should().NotContain("**Expected:**");
        body.Should().NotContain("**Actual:**");
        body.Should().NotContain("**CGM Source:**");
    }

    [Fact]
    public void BuildIssueBody_DataIssueTemplate_IncludesCgmSourceAndTimeRange()
    {
        var request = new CreateIssueRequest
        {
            Template = "data-issue",
            Title = "Missing data",
            Description = "No readings showing",
            CgmSource = "Dexcom G7",
            TimeRange = "Last 24 hours",
            DiagnosticInfo = "{\"userAgent\":\"test\"}",
        };

        var body = GitHubIssueService.BuildIssueBody(request, []);

        body.Should().Contain("**CGM Source:** Dexcom G7");
        body.Should().Contain("**Time Range:** Last 24 hours");
    }

    [Fact]
    public void BuildIssueBody_WithImages_IncludesScreenshotSection()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Visual bug",
            Description = "Chart looks wrong",
            DiagnosticInfo = "{\"userAgent\":\"test\"}",
        };

        var imageUrls = new List<string>
        {
            "https://example.com/image1.png",
            "https://example.com/image2.png",
        };

        var body = GitHubIssueService.BuildIssueBody(request, imageUrls);

        body.Should().Contain("## Screenshots");
        body.Should().Contain("![screenshot](https://example.com/image1.png)");
        body.Should().Contain("![screenshot](https://example.com/image2.png)");
    }

    [Fact]
    public void BuildIssueBody_WithDiagnosticInfo_IncludesDetailsBlock()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Test",
            Description = "Test description",
            DiagnosticInfo = "{\"userAgent\":\"Chrome\",\"screenSize\":\"1920x1080\"}",
        };

        var body = GitHubIssueService.BuildIssueBody(request, []);

        body.Should().Contain("<details>");
        body.Should().Contain("<summary>Diagnostic Info</summary>");
        body.Should().Contain("```json");
        body.Should().Contain("\"userAgent\":\"Chrome\"");
        body.Should().Contain("</details>");
    }

    [Fact]
    public void BuildIssueBody_EmptyOptionalFields_OmitsSections()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Minimal bug",
            Description = "Something broken",
            DiagnosticInfo = "{}",
        };

        var body = GitHubIssueService.BuildIssueBody(request, []);

        body.Should().Contain("## Description");
        body.Should().NotContain("## Steps to Reproduce");
        body.Should().NotContain("**Expected:**");
        body.Should().NotContain("**Actual:**");
        body.Should().NotContain("## Screenshots");
    }

    [Fact]
    public void HasLocalPat_WhenConfigured_ReturnsTrue()
    {
        var options = Options.Create(new GitHubIssueOptions { IssuesPat = "ghp_test123" });
        var service = new GitHubIssueService(
            new Mock<IHttpClientFactory>().Object,
            options,
            NullLogger<GitHubIssueService>.Instance);

        service.HasLocalPat.Should().BeTrue();
    }

    [Fact]
    public void HasLocalPat_WhenEmpty_ReturnsFalse()
    {
        var options = Options.Create(new GitHubIssueOptions { IssuesPat = "" });
        var service = new GitHubIssueService(
            new Mock<IHttpClientFactory>().Object,
            options,
            NullLogger<GitHubIssueService>.Instance);

        service.HasLocalPat.Should().BeFalse();
    }

    [Fact]
    public void HasLocalPat_WhenNull_ReturnsFalse()
    {
        var options = Options.Create(new GitHubIssueOptions { IssuesPat = null });
        var service = new GitHubIssueService(
            new Mock<IHttpClientFactory>().Object,
            options,
            NullLogger<GitHubIssueService>.Instance);

        service.HasLocalPat.Should().BeFalse();
    }

    [Fact]
    public void BuildIssueBody_DiagnosticInfoWithTripleBackticks_EscapesThem()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Test",
            Description = "Test",
            DiagnosticInfo = "some ```code``` here",
        };

        var body = GitHubIssueService.BuildIssueBody(request, []);

        body.Should().Contain("some ` ` `code` ` ` here");
        // The wrapping code fence should still be intact
        body.Should().Contain("```json");
    }

    [Fact]
    public void BuildIssueBody_ImagesAttachedButNotUploaded_NotesTheCount()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Visual bug",
            Description = "Chart looks wrong",
            DiagnosticInfo = "{}",
        };

        var body = GitHubIssueService.BuildIssueBody(request, [], attachedImageCount: 2);

        body.Should().NotContain("## Screenshots");
        body.Should().Contain("2 screenshot(s) were attached but could not be uploaded.");
    }

    [Fact]
    public void BuildIssueBody_AllImagesUploaded_HasNoUploadFailureNote()
    {
        var request = new CreateIssueRequest
        {
            Template = "bug",
            Title = "Visual bug",
            Description = "Chart looks wrong",
            DiagnosticInfo = "{}",
        };

        var body = GitHubIssueService.BuildIssueBody(
            request,
            ["https://example.com/image1.png"],
            attachedImageCount: 1);

        body.Should().Contain("## Screenshots");
        body.Should().NotContain("could not be uploaded");
    }

    [Theory]
    [InlineData("Screenshot 2026-07-16 at 15.54.43.png", "Screenshot2026-07-16at15.54.43.png")]
    [InlineData("../../evil.png", "....evil.png")]
    [InlineData("...", "screenshot")]
    [InlineData("", "screenshot")]
    public void SanitizeFileName_RestrictsToSafeCharacters(string input, string expected)
    {
        GitHubIssueService.SanitizeFileName(input).Should().Be(expected);
    }

    [Fact]
    public async Task CreateIssueAsync_WithImages_CommitsToAssetsBranchAndEmbedsUrls()
    {
        var requests = new List<(HttpRequestMessage Message, string Body)>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            requests.Add((request, body));

            if (request.Method == HttpMethod.Put)
            {
                return Respond(HttpStatusCode.Created,
                    """{"content":{"download_url":"https://raw.githubusercontent.com/nightscout/nocturne/support-assets/screenshots/test.png"}}""");
            }

            return Respond(HttpStatusCode.Created,
                """{"number":123,"html_url":"https://github.com/nightscout/nocturne/issues/123"}""");
        });

        var service = CreateService(handler);
        var imageBytes = "fake-png-bytes"u8.ToArray();
        using var imageStream = new MemoryStream(imageBytes);
        var images = new List<(string, string, Stream)>
        {
            ("Screenshot 2026-07-16 at 15.54.43.png", "image/png", imageStream),
        };

        var result = await service.CreateIssueAsync(BugRequest(), images, CancellationToken.None);

        result.IssueNumber.Should().Be(123);
        result.IssueUrl.Should().Be("https://github.com/nightscout/nocturne/issues/123");

        var upload = requests.Should().ContainSingle(r => r.Message.Method == HttpMethod.Put).Subject;
        upload.Message.RequestUri!.AbsolutePath.Should().StartWith("/repos/nightscout/nocturne/contents/screenshots/");
        upload.Message.RequestUri.AbsolutePath.Should().EndWith("-Screenshot2026-07-16at15.54.43.png");
        using (var doc = JsonDocument.Parse(upload.Body))
        {
            doc.RootElement.GetProperty("branch").GetString().Should().Be("support-assets");
            doc.RootElement.GetProperty("content").GetString().Should()
                .Be(Convert.ToBase64String(imageBytes));
        }

        var issue = requests.Should().ContainSingle(r => r.Message.Method == HttpMethod.Post).Subject;
        issue.Message.RequestUri!.AbsolutePath.Should().Be("/repos/nightscout/nocturne/issues");
        issue.Body.Should().Contain(
            "https://raw.githubusercontent.com/nightscout/nocturne/support-assets/screenshots/test.png");
    }

    [Fact]
    public async Task CreateIssueAsync_AssetsBranchUnset_SkipsUploadAndNotesAttachments()
    {
        var requests = new List<(HttpMethod Method, string Body)>();
        var handler = new StubHttpMessageHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, body));
            return Respond(HttpStatusCode.Created,
                """{"number":7,"html_url":"https://github.com/nightscout/nocturne/issues/7"}""");
        });

        var service = CreateService(handler, opts => opts.AssetsBranch = null);
        using var imageStream = new MemoryStream([1, 2, 3]);
        var images = new List<(string, string, Stream)>
        {
            ("shot.png", "image/png", imageStream),
        };

        var result = await service.CreateIssueAsync(BugRequest(), images, CancellationToken.None);

        result.IssueNumber.Should().Be(7);
        var issue = requests.Should().ContainSingle().Subject;
        issue.Method.Should().Be(HttpMethod.Post);
        issue.Body.Should().Contain("1 screenshot(s) were attached but could not be uploaded.");
    }

    [Fact]
    public async Task CreateIssueAsync_UploadFails_StillCreatesIssueWithNote()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(Respond(HttpStatusCode.NotFound, """{"message":"Branch not found"}"""));
            }
            return Task.FromResult(Respond(HttpStatusCode.Created,
                """{"number":8,"html_url":"https://github.com/nightscout/nocturne/issues/8"}"""));
        });

        var service = CreateService(handler);
        using var imageStream = new MemoryStream([1, 2, 3]);
        var images = new List<(string, string, Stream)>
        {
            ("shot.png", "image/png", imageStream),
        };

        var result = await service.CreateIssueAsync(BugRequest(), images, CancellationToken.None);

        result.IssueNumber.Should().Be(8);
    }

    private static CreateIssueRequest BugRequest() => new()
    {
        Template = "bug",
        Title = "Visual bug",
        Description = "Chart looks wrong",
        DiagnosticInfo = "{}",
    };

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static GitHubIssueService CreateService(
        HttpMessageHandler handler,
        Action<GitHubIssueOptions>? configure = null)
    {
        var options = new GitHubIssueOptions { IssuesPat = "ghp_test123" };
        configure?.Invoke(options);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new GitHubIssueService(
            factory.Object,
            Options.Create(options),
            NullLogger<GitHubIssueService>.Instance);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
