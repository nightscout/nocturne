using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services;
using Nocturne.Core.Contracts.Translations;
using Nocturne.Core.Models.Translations;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The contribution endpoints answer 502 for the ways reaching GitHub or the
/// relay fails, and answer nothing at all for anything else: a defect that
/// reads as the upstream's fault is a defect nobody goes looking for.
/// </summary>
public class TranslationsControllerFailureMappingTests
{
    private sealed class ThrowingHandler(Func<Exception> throws) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw throws();
    }

    private static TranslationsController CreateController(Func<Exception> throws)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new ThrowingHandler(throws)));

        // No PAT, so the contribution takes the relay path and the handler stands in
        // for nocturne.run.
        var service = new GitHubTranslationService(
            new GitHubPrClient(factory.Object, NullLogger<GitHubPrClient>.Instance),
            factory.Object,
            Options.Create(new GitHubContributionOptions()),
            NullLogger<GitHubTranslationService>.Instance);

        return new TranslationsController(
            service,
            Mock.Of<ITranslationDraftService>(),
            NullLogger<TranslationsController>.Instance)
        {
            ProblemDetailsFactory = new TestProblemDetailsFactory(),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static TranslationContributionRequest Request() => new()
    {
        Locale = "fr",
        Entries = [new TranslationEntryDto { MsgId = "Hello", Translations = ["Bonjour"] }],
        Contributor = new ContributionContributorDto { Name = "Jane Doe" },
    };

    [Fact]
    public async Task Relay_Transport_Failure_Is_A_Bad_Gateway()
    {
        var result = await CreateController(() => new HttpRequestException("connection refused"))
            .SubmitContribution(Request(), CancellationToken.None);

        var problem = (ObjectResult)result.Result!;
        problem.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task Relay_Timeout_Is_A_Bad_Gateway_While_The_Caller_Is_Still_Waiting()
    {
        // HttpClient reports its own timeout as a cancellation, so the caller's token
        // is what separates the two: here it was never cancelled.
        var result = await CreateController(() => new OperationCanceledException())
            .SubmitContribution(Request(), CancellationToken.None);

        var problem = (ObjectResult)result.Result!;
        problem.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task A_Caller_That_Went_Away_Gets_No_Answer()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreateController(() => new OperationCanceledException(cts.Token))
            .SubmitContribution(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_Unexpected_Failure_Is_Not_Dressed_Up_As_The_Upstream()
    {
        var act = () => CreateController(() => new NotSupportedException("defect"))
            .SubmitContribution(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
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
