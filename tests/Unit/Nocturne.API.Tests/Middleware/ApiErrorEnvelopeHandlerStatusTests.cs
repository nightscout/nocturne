using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.TestDoubles;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// A request Kestrel refuses while an envelope action reads it keeps its own status in the
/// version's envelope, and is not logged as a server error.
/// </summary>
[Trait("Category", "Unit")]
public class ApiErrorEnvelopeHandlerStatusTests
{
    private static async Task<(HttpContext Context, string Body, ListLogger<ApiErrorEnvelopeHandler> Logger)> HandleAsync(Exception exception)
    {
        var logger = new ListLogger<ApiErrorEnvelopeHandler>();
        var handler = new ApiErrorEnvelopeHandler(logger, Mock.Of<ProblemDetailsFactory>(), Options.Create(new JsonOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v3/entries";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ErrorEnvelopeAttribute()), "v3 entries"));

        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).Should().BeTrue();
        return (context, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()), logger);
    }

    [Fact]
    public async Task A_body_over_the_size_limit_answers_413_in_the_envelope_and_logs_a_warning()
    {
        var (context, body, logger) = await HandleAsync(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        body.Should().Be("""{"status":413,"message":"Payload Too Large"}""");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task Any_other_exception_is_still_a_500_logged_as_an_error()
    {
        var (context, body, logger) = await HandleAsync(new InvalidOperationException("boom"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Be("""{"status":500,"message":"Internal server error"}""");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }
}
