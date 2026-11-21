using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Health;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Pins the error envelope each API version answers with when an action throws.
/// </summary>
/// <remarks>
/// The three shapes are wire contracts, not implementation detail: V1 and V3 are what Nightscout
/// clients (AAPS, xDrip, Loop) parse, and the V1 body carries <c>ex.Message</c> that uploaders
/// surface to users, for an authenticated caller only. Nothing pinned them before, so they were
/// free to drift apart.
/// </remarks>
[Trait("Category", "Unit")]
public class ApiErrorEnvelopeTests : IClassFixture<ApiErrorEnvelopeTests.ThrowingServiceFactory>
{
    private const string BoomMessage = "boom from the service layer";
    private const string TwentyFourHexId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DeletedActivityId = "deleted-activity";

    private readonly HttpClient _client;
    private readonly ThrowingServiceFactory _factory;

    public ApiErrorEnvelopeTests(ThrowingServiceFactory factory)
    {
        _factory = factory;
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

    /// <summary>
    /// Nightscout serves v1 under <c>/api/v2</c> wherever v2 has no route of its own, so a v1
    /// resource read through a v2 path is served by the v1 action and answers the v1 envelope. The
    /// <c>.json</c> form checks the fallback runs after the suffix is stripped.
    /// </summary>
    [Theory]
    [InlineData($"/api/v2/entries/{TwentyFourHexId}")]
    [InlineData($"/api/v2/entries/{TwentyFourHexId}.json")]
    public async Task V2PathWithoutAV2Route_IsServedByV1_AndAnswersTheV1Envelope(string route)
    {
        var response = await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $$"""{"status":500,"message":"Internal server error","type":"internal","error":"{{BoomMessage}}"}""",
            await response.Content.ReadAsStringAsync()
        );
    }

    [Fact]
    public async Task V1_ThrowingActionOutsideEntries_AnswersTheNightscoutV1Envelope()
    {
        var response = await _client.GetAsync("/api/v1/iob");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $$"""{"status":500,"message":"Internal server error","type":"internal","error":"{{BoomMessage}}"}""",
            await response.Content.ReadAsStringAsync()
        );
    }

    /// <summary>
    /// <c>/api/versions</c> carries no version segment, so the envelope is the ProblemDetails body
    /// of an unversioned path.
    /// </summary>
    [Fact]
    public async Task V1_VersionsController_AnswersProblemDetailsWithoutTheExceptionMessage()
    {
        var response = await _client.GetAsync("/api/versions");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;
        Assert.Equal("Internal Server Error", root.GetProperty("title").GetString());
        Assert.Equal("Internal server error", root.GetProperty("detail").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.DoesNotContain(BoomMessage, raw);
    }

    [Theory]
    [InlineData("/api/v1/verifyauth", """{"status":200,"message":{"canRead":true""")]
    [InlineData("/api/v1/experiments/test", """{"status":"ok"}""")]
    public async Task V1_AttributedActionThatCannotThrow_KeepsItsSuccessBody(string route, string expectedStart)
    {
        var response = await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(expectedStart, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task V1_ActivityPutOfADeletedId_Answers409WithTheV1Envelope()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/activity/{DeletedActivityId}",
            new { type = "Walking" }
        );

        var blocked = new RecreationBlockedException(
            nameof(Activity),
            RecreationBlockedException.LegacyIdIdentity(DeletedActivityId)
        );
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            JsonSerializer.Serialize(
                new { status = 409, message = blocked.Message, type = "conflict", error = blocked.Message }
            ),
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
    /// A V4 controller that answers its 500 through the handler rather than a catch of its own:
    /// the body is the handler's one shape and does not carry what the service threw.
    /// </summary>
    [Theory]
    [InlineData("/api/v4/ui-settings")]
    [InlineData("/api/v4/ui-settings/features")]
    [InlineData("/api/v4/admin/deduplication/status/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public async Task V4_ConvertedController_AnswersProblemDetailsWithoutTheExceptionMessage(string route)
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

    /// <summary>
    /// V2 routes carry the V4 <c>ProblemDetails</c> body, serialised by
    /// <c>ApiErrorEnvelopeHandler</c> under the Nightscout options its remarks explain — no result
    /// filter can run once the pipeline has unwound to the exception handler.
    /// </summary>
    [Theory]
    [InlineData("/api/v2/authorization/request/some-access-token")]
    [InlineData("/api/v2/authorization/subjects")]
    [InlineData("/api/v2/authorization/roles")]
    [InlineData("/api/v2/summary")]
    [InlineData("/api/v2/loop/status")]
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

    [Fact]
    public async Task ThrowingAction_LogsTheRouteTemplate_NotTheCredentialInThePath()
    {
        const string token = "noc_credentialthatmustnotbelogged";

        await _client.GetAsync($"/api/v2/authorization/request/{token}");

        var messages = _factory.HandlerLogger.Entries
            .Where(e => e.Level == LogLevel.Error)
            .Select(e => e.Message)
            .ToList();
        messages.Should().Contain(m => m.Contains("api/v2/authorization/request/{accessToken}"));
        messages.Should().NotContain(m => m.Contains(token));
    }

    private static string Sha1Hex(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Replaces the services behind one action per envelope family with a stub that throws, so the
    /// request reaches the error path with a message the assertions can match exactly.
    /// </summary>
    public sealed class ThrowingServiceFactory : AuthenticationTestFactory
    {
        internal ListLogger<ApiErrorEnvelopeHandler> HandlerLogger { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILogger<ApiErrorEnvelopeHandler>>(HandlerLogger);
                var entries = new Mock<IEntryService>();
                entries
                    .Setup(s =>
                        s.GetEntryByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
                    )
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(entries.Object);
                services.AddSingleton(new Mock<IDocumentProcessingService>().Object);
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

                var uiSettings = new Mock<IUISettingsService>();
                uiSettings
                    .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(uiSettings.Object);

                var deduplication = new Mock<IDeduplicationAdmin>();
                deduplication
                    .Setup(s => s.GetRunStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(deduplication.Object);

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

                var iobCobReader = new Mock<IIobCobReader>();
                iobCobReader
                    .Setup(s =>
                        s.ReadTotalAsync(
                            It.IsAny<long>(),
                            It.IsAny<bool>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(iobCobReader.Object);

                var summary = new Mock<ISummaryService>();
                summary
                    .Setup(s =>
                        s.GetSummaryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                    )
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(summary.Object);

                var versions = new Mock<IVersionService>();
                versions
                    .Setup(s => s.GetSupportedVersionsAsync())
                    .ThrowsAsync(new InvalidOperationException(BoomMessage));
                services.AddSingleton(versions.Object);

                var loop = new Mock<ILoopService>();
                loop.Setup(s => s.GetConfigurationStatus())
                    .Throws(new InvalidOperationException(BoomMessage));
                services.AddSingleton(loop.Object);

                var activities = new Mock<IActivityService>();
                activities
                    .Setup(s => s.GetStoredActivitiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([]);
                services.AddSingleton(activities.Object);

                var decomposer = new Mock<IActivityDecomposer>();
                decomposer
                    .Setup(d => d.RequiredWriteScope(It.IsAny<Activity>()))
                    .Returns(Scope.TreatmentsReadWrite);
                decomposer
                    .Setup(d => d.IsDeletedByUserAsync(DeletedActivityId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
                services.AddSingleton(decomposer.Object);
            });
        }
    }
}
