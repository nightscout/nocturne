using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Filters;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Filters;

/// <summary>
/// The filter handles the refusal before <see cref="Middleware.ApiErrorEnvelopeHandler"/> could, so an
/// <see cref="ErrorEnvelopeAttribute"/> action has to get its version's envelope from the filter.
/// </summary>
[Trait("Category", "Unit")]
public class RecreationBlockedFilterEnvelopeTests
{
    private static readonly RecreationBlockedException Blocked =
        RecreationBlockedException.ForSyncKey("Bolus", "aaps", "sync-1");

    [Fact]
    public void V1EnvelopeAction_AnswersTheV1EnvelopeWith409()
    {
        var result = Run("/api/v1/treatments", envelope: true).Should().BeOfType<JsonResult>().Subject;

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        Members(result).Should().Equal(
            ("status", "409"), ("message", Blocked.Message), ("type", "conflict"), ("error", Blocked.Message));
    }

    [Fact]
    public void V3EnvelopeAction_AnswersTheV3EnvelopeWith409()
    {
        var result = Run("/api/v3/treatments", envelope: true).Should().BeOfType<JsonResult>().Subject;

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        Members(result).Should().Equal(("status", "409"), ("message", Blocked.Message));
    }

    [Fact]
    public void ActionWithoutTheEnvelope_KeepsTheProblemDetails()
    {
        var result = Run("/api/v1/treatments", envelope: false).Should().BeOfType<ObjectResult>().Subject;

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        result.ContentTypes.Should().ContainSingle().Which.Should().Be("application/problem+json");
        result.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(Blocked.Message);
    }

    private static IActionResult Run(string path, bool envelope)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = envelope ? [new ErrorEnvelopeAttribute()] : [],
        };
        var context = new ExceptionContext(new ActionContext(httpContext, new RouteData(), descriptor), [])
        {
            Exception = Blocked,
        };

        new RecreationBlockedFilter(EchoingProblemDetailsFactory(), Options.Create(new JsonOptions()))
            .OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        return context.Result!;
    }

    private static List<(string Name, string Value)> Members(JsonResult result)
    {
        var json = JsonSerializer.Serialize(result.Value, (JsonSerializerOptions)result.SerializerSettings!);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()))
            .ToList();
    }

    private static ProblemDetailsFactory EchoingProblemDetailsFactory()
    {
        var factory = new Mock<ProblemDetailsFactory>();
        factory
            .Setup(f => f.CreateProblemDetails(
                It.IsAny<HttpContext>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((HttpContext _, int? status, string? title, string? type, string? detail, string? instance) =>
                new ProblemDetails { Status = status, Title = title, Type = type, Detail = detail, Instance = instance });
        return factory.Object;
    }
}
