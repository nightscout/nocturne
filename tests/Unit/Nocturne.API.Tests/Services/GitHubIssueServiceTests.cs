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
}
