using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services;
using Nocturne.Core.Contracts.Content;
using Nocturne.Core.Models.Content;
using Nocturne.Core.Models.Translations;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// <see cref="ContentContributionsController.Validate"/> is the only gate in
/// front of the anonymous relay ingress: everything it lets through is
/// committed to a file and a pull request on the upstream repository.
/// </summary>
public class ContentContributionsControllerValidationTests
{
    private const string ValidPath = "src/Web/packages/portal/src/content/blog/my-post.svx";

    private static ContentContributionsController CreateController(
        IContentContributionService? service = null) =>
        new(
            service ?? Mock.Of<IContentContributionService>(),
            NullLogger<ContentContributionsController>.Instance)
        {
            ProblemDetailsFactory = new TestProblemDetailsFactory(),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static ContentContributionRequest Request(
        string path = ValidPath,
        string content = "---\ntitle: My Post\n---\n\nBody",
        string title = "My Post",
        string name = "Jane Doe",
        string? note = null) => new()
        {
            Path = path,
            Content = content,
            Title = title,
            Contributor = new ContributionContributorDto { Name = name },
            Note = note,
        };

    private static string? Reject(ContentContributionRequest request)
    {
        var result = CreateController().Validate(request);
        if (result is null) return null;
        result.StatusCode.Should().Be(400);
        return ((ProblemDetails)result.Value!).Detail;
    }

    [Fact]
    public void Validate_Accepts_A_Well_Formed_Contribution()
    {
        Reject(Request()).Should().BeNull();
    }

    // --- path ------------------------------------------------------------

    [Theory]
    [InlineData("src/API/Nocturne.API/Program.cs")]
    [InlineData(".github/workflows/deploy.yml")]
    [InlineData("src/Web/packages/portal/src/content/blog/../../../secret.svx")]
    public void Validate_Rejects_Paths_Outside_Portal_Content(string path)
    {
        Reject(Request(path: path)).Should().StartWith("Path must be");
    }

    [Fact]
    public void Validate_Accepts_A_Path_At_The_Length_Cap()
    {
        Reject(Request(path: PathOfLength(ContributionValidation.MaxPathLength)))
            .Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_A_Path_One_Character_Over_The_Cap()
    {
        Reject(Request(path: PathOfLength(ContributionValidation.MaxPathLength + 1)))
            .Should().StartWith("Path must be");
    }

    private static string PathOfLength(int length)
    {
        const string prefix = "src/Web/packages/portal/src/content/blog/";
        const string suffix = ".svx";
        return prefix + new string('a', length - prefix.Length - suffix.Length) + suffix;
    }

    // --- content ---------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_Empty_Content(string content)
    {
        Reject(Request(content: content)).Should().StartWith("Content is required");
    }

    [Fact]
    public void Validate_Accepts_Content_At_The_Byte_Cap()
    {
        var content = new string('a', ContentContributionsController.MaxContentBytes);

        Reject(Request(content: content)).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_Content_One_Byte_Over_The_Cap()
    {
        var content = new string('a', ContentContributionsController.MaxContentBytes + 1);

        Reject(Request(content: content)).Should().StartWith("Content is required");
    }

    [Fact]
    public void The_Content_Cap_Counts_Bytes_Not_Characters()
    {
        // "é" is one char but two UTF-8 bytes, so a string half the cap in
        // characters is exactly the cap in bytes — and one more char is over.
        // A Length check would admit twice the payload GitHub receives.
        var atCap = new string('é', ContentContributionsController.MaxContentBytes / 2);
        var overCap = atCap + 'é';

        Reject(Request(content: atCap)).Should().BeNull();
        Reject(Request(content: overCap)).Should().StartWith("Content is required");
    }

    // --- title -----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_Empty_Titles(string title)
    {
        Reject(Request(title: title)).Should().StartWith("Title is required");
    }

    [Fact]
    public void Validate_Accepts_A_Title_At_The_Length_Cap()
    {
        var title = new string('t', ContentContributionsController.MaxTitleLength);

        Reject(Request(title: title)).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_A_Title_One_Character_Over_The_Cap()
    {
        var title = new string('t', ContentContributionsController.MaxTitleLength + 1);

        Reject(Request(title: title)).Should().StartWith("Title is required");
    }

    [Theory]
    [InlineData("My\nPost")]
    [InlineData("My\rPost")]
    [InlineData("My\tPost")]
    [InlineData("My\u0000Post")]
    [InlineData("My\u007fPost")]
    public void Validate_Rejects_Control_Characters_In_The_Title(string title)
    {
        // The title becomes the PR title, so a newline would let a submitter
        // write the body from the title field.
        Reject(Request(title: title)).Should().StartWith("Title is required");
    }

    // --- contributor -----------------------------------------------------

    [Fact]
    public void Validate_Rejects_A_Contributor_The_Shared_Validator_Rejects()
    {
        Reject(Request(name: "")).Should().StartWith("Contributor name is required");
    }

    [Fact]
    public void Validate_Rejects_An_Overlong_Note()
    {
        var note = new string('n', ContributionValidation.MaxNoteLength + 1);

        Reject(Request(note: note)).Should().StartWith("Note must be under");
    }

    // --- relay gate ------------------------------------------------------
    // The relay is [AllowAnonymous]. This gate is the only thing keeping an
    // instance that did not opt in from exposing an anonymous PR-opening
    // endpoint. The two operands behind AcceptsRelay are pinned on the
    // service, in GitHubContentServiceTests.

    [Fact]
    public async Task Relay_Is_NotFound_When_The_Service_Does_Not_Accept_Relay()
    {
        var service = new Mock<IContentContributionService>();

        var result = await CreateController(service.Object)
            .AcceptRelayedContribution(Request(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        service.Verify(
            s => s.SubmitAsync(It.IsAny<ContentContributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Relay_Submits_When_Opted_In_And_Able_To_Open_Prs()
    {
        var service = new Mock<IContentContributionService>();
        service.SetupGet(s => s.AcceptsRelay).Returns(true);
        service
            .Setup(s => s.SubmitAsync(It.IsAny<ContentContributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentContributionResponse { PrNumber = 7, PrUrl = "https://x/7", Created = true });

        var result = await CreateController(service.Object)
            .AcceptRelayedContribution(Request(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Relay_Validates_Before_Reaching_The_Service()
    {
        var service = new Mock<IContentContributionService>();
        service.SetupGet(s => s.AcceptsRelay).Returns(true);

        var result = await CreateController(service.Object)
            .AcceptRelayedContribution(Request(path: "src/API/Nocturne.API/Program.cs"), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
        service.Verify(
            s => s.SubmitAsync(It.IsAny<ContentContributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class TestProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null,
            string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };
    }
}
