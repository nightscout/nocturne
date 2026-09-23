using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Extensions;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The two ways an issue reaches GitHub: <c>/issues</c> for this instance's own signed-in
/// reporters, and the anonymous <c>/relay</c> that instances without a PAT forward to. Everything
/// the relay accepts is filed, and its screenshots committed, on the operator's PAT.
/// </summary>
[Trait("Category", "Unit")]
public class SupportControllerIssueTests
{
    private const string Pat = "ghp_test123";
    private const string RelayUrl = "https://relay.example/api/v4/support/relay";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly List<(HttpRequestMessage Message, string Body)> _sent = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = DefaultResponse;

    private static HttpResponseMessage DefaultResponse(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Put)
            return Json(HttpStatusCode.Created,
                """{"content":{"download_url":"https://raw.example/screenshots/shot.png"}}""");

        return request.RequestUri!.AbsoluteUri == RelayUrl
            ? Json(HttpStatusCode.Created, """{"issueNumber":42,"issueUrl":"https://github.com/o/r/issues/42"}""")
            : Json(HttpStatusCode.Created, """{"number":7,"html_url":"https://github.com/o/r/issues/7"}""");
    }

    private SupportController CreateController(string? pat = Pat, bool acceptRelay = true)
    {
        var options = Options.Create(new GitHubIssueOptions
        {
            IssuesPat = pat,
            AcceptRelayedIssues = acceptRelay,
            RelayUrl = RelayUrl,
            Owner = "o",
            Repo = "r",
        });

        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            _sent.Add((request, body));
            return _respond(request);
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var service = new GitHubIssueService(factory.Object, options, NullLogger<GitHubIssueService>.Instance);

        return new SupportController(
            service,
            Mock.Of<ISupportDiagnosticsService>(),
            options,
            Options.Create(new OperatorConfiguration()),
            NullLogger<SupportController>.Instance)
        {
            ProblemDetailsFactory = new TestProblemDetailsFactory(),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public record Form(
        string Template = "bug",
        string Title = "[test] Chart is blank",
        string Description = "Synthetic description",
        string DiagnosticInfo = "{\"userAgent\":\"test\"}",
        List<IFormFile>? Images = null);

    private static Task<ActionResult<CreateIssueResponse>> Relay(SupportController controller, Form form) =>
        controller.AcceptRelayedIssue(
            form.Template, form.Title, form.Description, "1. Open", "Works", "Blank",
            null, null, form.DiagnosticInfo, form.Images, CancellationToken.None);

    private static Task<ActionResult<CreateIssueResponse>> Direct(SupportController controller, Form form) =>
        controller.CreateIssue(
            form.Template, form.Title, form.Description, "1. Open", "Works", "Blank",
            null, null, form.DiagnosticInfo, form.Images, CancellationToken.None);

    private static IFormFile Image(
        string contentType = "image/png", long size = 64, byte[]? header = null, string name = "shot.png")
    {
        header ??= contentType switch
        {
            "image/jpeg" => [0xFF, 0xD8, 0xFF, 0xE0],
            "image/gif" => "GIF89a"u8.ToArray(),
            "image/webp" => [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8],
            _ => PngSignature,
        };
        var bytes = new byte[Math.Max(size, header.Length)];
        header.CopyTo(bytes, 0);

        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "images", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static List<IFormFile> Images(int count, long size = 64) =>
        [.. Enumerable.Range(0, count).Select(i => Image(size: size, name: $"shot{i}.png"))];

    private static int StatusOf(ActionResult<CreateIssueResponse> result) =>
        result.Result.Should().BeAssignableTo<ObjectResult>().Subject.StatusCode!.Value;

    private static string DetailOf(ActionResult<CreateIssueResponse> result) =>
        result.Result.Should().BeAssignableTo<ObjectResult>().Subject.Value
            .Should().BeOfType<ProblemDetails>().Subject.Detail!;

    // --- relay gate --------------------------------------------------------
    // The relay is [AllowAnonymous]; this gate is all that keeps an instance which did not opt in
    // from filing issues for anyone on its operator's PAT. The operands of AcceptsRelay are pinned
    // in GitHubIssueServiceTests.

    [Fact]
    public async Task Relay_IsNotFound_WhenNotOptedIn()
    {
        var result = await Relay(CreateController(acceptRelay: false), new Form());

        result.Result.Should().BeOfType<NotFoundResult>();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_IsNotFound_WhenOptedInWithoutAPat()
    {
        var result = await Relay(CreateController(pat: null, acceptRelay: true), new Form());

        result.Result.Should().BeOfType<NotFoundResult>();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_IsNotFound_BeforeValidating()
    {
        var result = await Relay(CreateController(acceptRelay: false), new Form(Template: "nope"));

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Relay_FilesTheIssueOnTheLocalPat_WhenOptedIn()
    {
        var result = await Relay(CreateController(), new Form());

        StatusOf(result).Should().Be(201);
        result.Result.As<ObjectResult>().Value.Should().BeEquivalentTo(
            new CreateIssueResponse { IssueNumber = 7, IssueUrl = "https://github.com/o/r/issues/7" });

        var (message, body) = _sent.Should().ContainSingle().Subject;
        message.Method.Should().Be(HttpMethod.Post);
        message.RequestUri!.AbsoluteUri.Should().Be("https://api.github.com/repos/o/r/issues");
        message.Headers.Authorization!.Scheme.Should().Be("Bearer");
        message.Headers.Authorization.Parameter.Should().Be(Pat);
        body.Should().Contain("Synthetic description");
    }

    [Fact]
    public async Task Relay_CommitsAScreenshotToTheAssetsBranch()
    {
        var result = await Relay(CreateController(), new Form(Images: [Image()]));

        StatusOf(result).Should().Be(201);
        _sent.Select(s => s.Message.Method).Should().Equal(HttpMethod.Put, HttpMethod.Post);
        _sent[0].Message.RequestUri!.AbsolutePath.Should().StartWith("/repos/o/r/contents/screenshots/");
        _sent[1].Body.Should().Contain("https://raw.example/screenshots/shot.png");
    }

    [Fact]
    public async Task Relay_GitHubFailure_Is502()
    {
        _respond = _ => Json(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}""");

        var result = await Relay(CreateController(), new Form());

        StatusOf(result).Should().Be(502);
        DetailOf(result).Should().StartWith("Failed to create issue");
    }

    // --- relay validation ----------------------------------------------------

    public static TheoryData<string, Form, string> InvalidForms => new()
    {
        { "unknown template", new Form(Template: "exploit"), "Invalid template: exploit" },
        { "empty title", new Form(Title: " "), "Title is required and must be under 256 characters" },
        { "overlong title", new Form(Title: new string('t', 257)), "Title is required and must be under 256 characters" },
        { "empty description", new Form(Description: ""), "Description is required" },
        { "empty diagnostics", new Form(DiagnosticInfo: "  "), "Diagnostic info is required" },
    };

    [Theory]
    [MemberData(nameof(InvalidForms))]
    public async Task Relay_RejectsWhatTheDirectPathRejects(string because, Form form, string detail)
    {
        var relayed = await Relay(CreateController(), form);
        var direct = await Direct(CreateController(), form);

        StatusOf(relayed).Should().Be(400, because);
        DetailOf(relayed).Should().Be(detail);
        StatusOf(direct).Should().Be(400, because);
        DetailOf(direct).Should().Be(detail);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_AcceptsATitleAtTheCap()
    {
        var result = await Relay(CreateController(), new Form(Title: new string('t', 256)));

        StatusOf(result).Should().Be(201);
    }

    [Fact]
    public async Task Relay_RejectsMoreImagesThanItsCap()
    {
        var count = SupportController.RelayImageLimits.MaxCount;

        StatusOf(await Relay(CreateController(), new Form(Images: Images(count)))).Should().Be(201);

        _sent.Clear();
        var result = await Relay(CreateController(), new Form(Images: Images(count + 1)));

        StatusOf(result).Should().Be(400);
        DetailOf(result).Should().Be($"Maximum {count} images allowed");
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_RejectsAnImageTheDirectPathAccepts()
    {
        var size = SupportController.RelayImageLimits.MaxBytesEach + 1;

        var direct = await Direct(CreateController(), new Form(Images: [Image(size: size)]));
        StatusOf(direct).Should().Be(201);

        _sent.Clear();
        var relayed = await Relay(CreateController(), new Form(Images: [Image(size: size)]));

        StatusOf(relayed).Should().Be(400);
        DetailOf(relayed).Should().Be("Image shot.png exceeds 5 MB limit");
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_RejectsImagesOverItsTotal_ThatTheDirectPathAccepts()
    {
        var each = SupportController.RelayImageLimits.MaxBytesEach;
        var count = (int)(SupportController.RelayImageLimits.MaxTotalBytes / each) + 1;
        count.Should().BeLessThanOrEqualTo(SupportController.RelayImageLimits.MaxCount,
            "the total cap has to bind before the count cap for this case to test it");

        StatusOf(await Direct(CreateController(), new Form(Images: Images(count, each)))).Should().Be(201);

        _sent.Clear();
        var result = await Relay(CreateController(), new Form(Images: Images(count, each)));

        StatusOf(result).Should().Be(400);
        DetailOf(result).Should().Be("Images exceed 10 MB in total");
        _sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public async Task Relay_RejectsAContentTypeOutsideTheAllowList(string contentType)
    {
        var result = await Relay(CreateController(),
            new Form(Images: [Image(contentType, header: PngSignature)]));

        StatusOf(result).Should().Be(400);
        DetailOf(result).Should().Be("Image shot.png must be PNG, JPEG, WebP, or GIF");
        _sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task Relay_AcceptsEachAllowedTypeWithItsSignature(string contentType)
    {
        var result = await Relay(CreateController(), new Form(Images: [Image(contentType)]));

        StatusOf(result).Should().Be(201);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task Relay_RejectsAFileWhoseBytesAreNotItsDeclaredType(string contentType)
    {
        var html = Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");

        var result = await Relay(CreateController(), new Form(Images: [Image(contentType, header: html)]));

        StatusOf(result).Should().Be(400);
        DetailOf(result).Should().Be("Image shot.png must be PNG, JPEG, WebP, or GIF");
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_RejectsAnImageShorterThanItsSignature()
    {
        var result = await Relay(CreateController(),
            new Form(Images: [Image("image/webp", header: "RIFF"u8.ToArray(), size: 4)]));

        StatusOf(result).Should().Be(400);
    }

    [Fact]
    public void TheRelayLimitsAreNoLooserThanTheDirectOnes()
    {
        var relay = SupportController.RelayImageLimits;
        var direct = SupportController.DirectImageLimits;

        relay.MaxCount.Should().BeLessThanOrEqualTo(direct.MaxCount);
        relay.MaxBytesEach.Should().BeLessThan(direct.MaxBytesEach);
        relay.MaxTotalBytes.Should().BeLessThan(direct.MaxTotalBytes);
    }

    // --- /issues ---------------------------------------------------------------

    [Fact]
    public async Task Issues_WithAPat_FilesLocally()
    {
        var result = await Direct(CreateController(acceptRelay: false), new Form());

        StatusOf(result).Should().Be(201);
        _sent.Should().ContainSingle().Which.Message.RequestUri!.AbsoluteUri
            .Should().Be("https://api.github.com/repos/o/r/issues");
    }

    [Fact]
    public async Task Issues_WithoutAPat_RelaysToTheConfiguredUrlWithNoCredential()
    {
        var result = await Direct(CreateController(pat: null), new Form(Images: [Image()]));

        StatusOf(result).Should().Be(201);
        result.Result.As<ObjectResult>().Value.Should().BeEquivalentTo(
            new CreateIssueResponse { IssueNumber = 42, IssueUrl = "https://github.com/o/r/issues/42" });

        var (message, body) = _sent.Should().ContainSingle().Subject;
        message.Method.Should().Be(HttpMethod.Post);
        message.RequestUri!.AbsoluteUri.Should().Be(RelayUrl);
        message.Headers.Authorization.Should().BeNull();
        message.Content!.Headers.ContentType!.MediaType.Should().Be("multipart/form-data");
        body.Should().Contain("name=template").And.Contain("[test] Chart is blank")
            .And.Contain("name=diagnosticInfo").And.Contain("name=images; filename=shot.png");
    }

    [Fact]
    public async Task Issues_WithoutAPat_HoldsTheSubmissionToTheRelayLimits()
    {
        var size = SupportController.RelayImageLimits.MaxBytesEach + 1;

        var result = await Direct(CreateController(pat: null), new Form(Images: [Image(size: size)]));

        StatusOf(result).Should().Be(400);
        _sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Issues_RelayRefusal_Is502(HttpStatusCode status)
    {
        _respond = _ => Json(status, """{"title":"nope"}""");

        var result = await Direct(CreateController(pat: null), new Form());

        StatusOf(result).Should().Be(502);
        DetailOf(result).Should().Be("Failed to create issue. Try again or report directly on GitHub.");
    }

    [Fact]
    public async Task Issues_RelayUnreachable_Is502()
    {
        _respond = _ => throw new HttpRequestException("connection refused");

        var result = await Direct(CreateController(pat: null), new Form());

        StatusOf(result).Should().Be(502);
    }

    [Fact]
    public async Task Issues_GitHubFailure_Is502()
    {
        _respond = _ => Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var result = await Direct(CreateController(), new Form());

        StatusOf(result).Should().Be(502);
    }

    // --- attributes -------------------------------------------------------------

    private static MethodInfo ActionMethod(string name) =>
        typeof(SupportController).GetMethod(name)!;

    [Fact]
    public void Issues_RequiresAuthentication()
    {
        typeof(SupportController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        ActionMethod(nameof(SupportController.CreateIssue))
            .GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }

    [Fact]
    public void Relay_IsAnonymousRateLimitedAndOffTheClient()
    {
        var relay = ActionMethod(nameof(SupportController.AcceptRelayedIssue));

        relay.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("relay");
        relay.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        relay.GetCustomAttribute<ApiExplorerSettingsAttribute>()!.IgnoreApi.Should().BeTrue();

        var policy = relay.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName;
        policy.Should().Be(ActionMethod(nameof(SupportController.CreateIssue))
            .GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        ServiceRegistrationExtensions.ClientAddressPolicies.Select(p => p.Policy).Should().Contain(policy!);
    }

    [Fact]
    public void Relay_RequestSizeLimit_AdmitsItsImagesButNotTheDirectCeiling()
    {
        var limit = ActionMethod(nameof(SupportController.AcceptRelayedIssue))
            .GetCustomAttribute<RequestSizeLimitAttribute>();
        limit.Should().NotBeNull();

        var bytes = ((IRequestSizeLimitMetadata)limit!).MaxRequestBodySize!.Value;
        bytes.Should().BeGreaterThan(SupportController.RelayImageLimits.MaxTotalBytes);
        bytes.Should().BeLessThan(SupportController.DirectImageLimits.MaxTotalBytes);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
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
