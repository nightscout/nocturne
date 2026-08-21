using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// The Glooko fetch path spends its attempts and then throws, and a 403 on a patient-scoped URL
/// abandons the remaining attempts because the stale patient code will 403 again unchanged.
/// </summary>
public class GlookoFetchRetryTests
{
    private const string Url = "/api/v2/pumps/temporary_basals?patient=eu-west-1-indigo-killdeer-4650";

    [Fact]
    public async Task FetchWithRetry_WhenEveryAttemptFails_ThrowsAfterSpendingEveryAttempt()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var (service, delays) = BuildService(handler);

        var act = () => service.FetchFromGlookoEndpointWithRetry(Context(), Url);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*500*");
        handler.Requests.Should().Be(3, "three attempts, not three retries on top of a first try");
        delays.Verify(d => d.ApplyRetryDelayAsync(It.IsAny<int>()), Times.Exactly(2));
    }

    [Fact]
    public async Task FetchWithRetry_WhenForbidden_BailsOutWithoutSpendingTheRemainingAttempts()
    {
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"status\":403,\"code\":\"data_cant_view\"}", Encoding.UTF8, "application/json"),
        });
        var (service, delays) = BuildService(handler);

        var act = () => service.FetchFromGlookoEndpointWithRetry(Context(), Url);

        await act.Should().ThrowAsync<GlookoDataForbiddenException>();
        handler.Requests.Should().Be(1, "the patient code is part of the URL, so retrying it unchanged would 403 again");
        delays.Verify(d => d.ApplyRetryDelayAsync(It.IsAny<int>()), Times.Never);
    }

    private static GlookoSyncContext Context()
    {
        var config = new GlookoConnectorConfiguration
        {
            ConnectSource = ConnectSource.Glooko,
            Email = "user@example.com",
            Password = "secret",
            Server = GlookoConstants.RegionEU,
        };

        return new GlookoSyncContext(config, DataSources.GlookoConnector, NullLogger.Instance)
        {
            SessionCookie = "_logbook-web_session=sess",
        };
    }

    private static (GlookoConnectorService service, Mock<IRetryDelayStrategy> delays) BuildService(
        CountingHandler handler)
    {
        var delays = new Mock<IRetryDelayStrategy>();
        delays.Setup(d => d.ApplyRetryDelayAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var service = new GlookoConnectorService(
            new HttpClient(handler),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            delays.Object,
            Mock.Of<IRateLimitingStrategy>(),
            new StaticGlookoTokenProvider());

        return (service, delays);
    }

    private sealed class CountingHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(response());
        }
    }
}
