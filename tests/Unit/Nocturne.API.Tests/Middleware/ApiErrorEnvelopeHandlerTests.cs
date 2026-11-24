using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Pins who gets the exception message in the V1 <c>error</c> member. The pipeline factory in
/// <see cref="ApiErrorEnvelopeTests"/> answers a request with no credential 401 before an action
/// runs, because only a share host resolves the public reader, so the handler is run directly. A
/// share reader and a refused credential hold a context that is not authenticated; a request that
/// failed before authentication holds none.
/// </summary>
[Trait("Category", "Unit")]
public class ApiErrorEnvelopeHandlerTests
{
    private const string Message = "relation entries does not exist";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V1_WithoutACredential_AnswersTheFixedTextInError(bool holdsAnUnauthenticatedContext)
    {
        var body = await Handle(holdsAnUnauthenticatedContext ? AuthContext.Unauthenticated() : null);

        Assert.Equal(
            """{"status":500,"message":"Internal server error","type":"internal","error":"Internal server error"}""",
            body
        );
    }

    [Fact]
    public async Task V1_WithACredential_AnswersTheExceptionMessageInError()
    {
        var body = await Handle(new AuthContext { IsAuthenticated = true });

        Assert.Equal(
            """{"status":500,"message":"Internal server error","type":"internal","error":"relation entries does not exist"}""",
            body
        );
    }

    private static async Task<string> Handle(AuthContext? caller)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/iob";
        httpContext.Response.Body = new MemoryStream();
        httpContext.SetEndpoint(
            new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/api/v1/iob"),
                0,
                new EndpointMetadataCollection(new ErrorEnvelopeAttribute()),
                "iob"
            )
        );
        if (caller is not null)
        {
            httpContext.SetAuthContext(caller);
        }

        var handler = new ApiErrorEnvelopeHandler(
            NullLogger<ApiErrorEnvelopeHandler>.Instance,
            new EchoingProblemDetailsFactory(),
            Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions())
        );

        Assert.True(
            await handler.TryHandleAsync(
                httpContext,
                new InvalidOperationException(Message),
                CancellationToken.None
            )
        );

        httpContext.Response.Body.Position = 0;
        return Encoding.UTF8.GetString(((MemoryStream)httpContext.Response.Body).ToArray());
    }
}
