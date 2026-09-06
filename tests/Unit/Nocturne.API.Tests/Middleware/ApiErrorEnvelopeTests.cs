using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Legacy;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Pins the error envelope each API version answers with when an action throws.
/// </summary>
/// <remarks>
/// The three shapes are wire contracts, not implementation detail: V1 and V3 are what Nightscout
/// clients (AAPS, xDrip, Loop) parse, and the V1 body carries <c>ex.Message</c> that uploaders
/// surface to users. Nothing pinned them before, so they were free to drift apart.
/// </remarks>
[Trait("Category", "Unit")]
public class ApiErrorEnvelopeTests : IClassFixture<ApiErrorEnvelopeTests.ThrowingServiceFactory>
{
    private const string BoomMessage = "boom from the service layer";
    private const string TwentyFourHexId = "aaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly HttpClient _client;

    public ApiErrorEnvelopeTests(ThrowingServiceFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            "api-secret",
            Sha1Hex(AuthenticationTestFactory.ApiSecret)
        );
    }

    [Fact]
    public async Task V1_ThrowingAction_AnswersTheNightscoutV1Envelope()
    {
        var response = await _client.GetAsync($"/api/v1/entries/{TwentyFourHexId}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $$"""{"status":500,"message":"Internal server error","type":"internal","error":"{{BoomMessage}}"}""",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task V3_ThrowingAction_AnswersTheNightscoutV3Envelope()
    {
        var response = await _client.GetAsync($"/api/v3/entries/{TwentyFourHexId}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """{"status":500,"message":"Internal server error"}""",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task V4_ThrowingAction_AnswersProblemDetails()
    {
        var response = await _client.GetAsync("/api/v4/body-weight");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("Internal Server Error", root.GetProperty("title").GetString());
        Assert.Equal("Internal server error", root.GetProperty("detail").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal(
            "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            root.GetProperty("type").GetString()
        );
        Assert.False(root.TryGetProperty("message", out _));
    }

    /// <summary>
    /// V2 routes carry the V4 <c>ProblemDetails</c> body, serialised by
    /// <c>ApiErrorEnvelopeHandler</c> under the Nightscout options its remarks explain — no result
    /// filter can run once the pipeline has unwound to the exception handler.
    /// </summary>
    [Theory]
    [InlineData("/api/v2/authorization/request/some-access-token")]
    [InlineData("/api/v2/authorization/subjects")]
    [InlineData("/api/v2/authorization/roles")]
    public async Task V2_ThrowingAction_AnswersProblemDetailsWithoutTheExceptionMessage(string route)
    {
        var response = await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;
        Assert.Equal("Internal Server Error", root.GetProperty("title").GetString());
        Assert.Equal("Internal server error", root.GetProperty("detail").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.DoesNotContain(BoomMessage, raw);
    }

    private static string Sha1Hex(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Replaces the services behind one action per envelope family with a stub that throws, so the
    /// request reaches the error path with a message the assertions can match exactly.
    /// </summary>
    public sealed class ThrowingServiceFactory : AuthenticationTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                var entries = new Mock<IEntryService>();
                entries
                    .Setup(s =>
                        s.GetEntryByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
                    )
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(entries.Object);
                services.AddSingleton(new Mock<IDocumentProcessingService>().Object);
                services.AddSingleton(new Mock<IProcessingStatusService>().Object);
                services.AddSingleton(new Mock<ICanonicalAlertEvaluator>().Object);

                var bodyWeight = new Mock<IBodyWeightService>();
                bodyWeight
                    .Setup(s =>
                        s.GetBodyWeightsAsync(
                            It.IsAny<int>(),
                            It.IsAny<int>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(bodyWeight.Object);

                var authorization = new Mock<IAuthorizationService>();
                authorization
                    .Setup(s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()))
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                authorization
                    .Setup(s => s.GetAllSubjectsAsync())
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                authorization
                    .Setup(s => s.GetAllRolesAsync())
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(authorization.Object);
            });
        }
    }
}
