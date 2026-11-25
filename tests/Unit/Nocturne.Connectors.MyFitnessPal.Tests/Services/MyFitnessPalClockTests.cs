using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.MyFitnessPal.Tests.Services;

public class MyFitnessPalClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Token_ExpiresFiveMinutesBeforeItsLifetimeEnds()
    {
        var clock = new FakeTimeProvider(Now);
        var fixture = new Fixture(clock, []);

        await fixture.TokenProvider.GetValidTokenAsync(fixture.Config, CancellationToken.None);

        fixture.TokenProvider.TokenExpiresAt.Should().Be(Now.UtcDateTime.AddMinutes(115));
        clock.Advance(TimeSpan.FromMinutes(115) - TimeSpan.FromSeconds(1));
        fixture.TokenProvider.IsTokenExpired.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        fixture.TokenProvider.IsTokenExpired.Should().BeTrue();
    }

    [Fact]
    public async Task Sync_ImportsOnlyEntriesBetweenTheLookbackStartAndOneDayAhead()
    {
        var fixture = new Fixture(new FakeTimeProvider(Now),
        [
            ("before-window", "2026-07-12"),
            ("lookback-day", "2026-07-14"),
            ("later-today", "2026-07-20"),
            ("beyond-headroom", "2026-07-22"),
        ]);

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] }, fixture.Config, CancellationToken.None);

        fixture.Imported.Select(e => e.ExternalEntryId).Should()
            .BeEquivalentTo("lookback-day", "later-today");
    }

    [Fact]
    public async Task Sync_StampsTheFullWalkAtTheClockInstant()
    {
        var fixture = new Fixture(new FakeTimeProvider(Now), [("lookback-day", "2026-07-14")]);

        await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Food] }, fixture.Config, CancellationToken.None);

        fixture.State.Resume[MyFitnessPalConstants.FullWalkStamp].Should().Be(Now.UtcDateTime);
    }

    internal sealed class Fixture
    {
        internal MyFitnessPalAuthTokenProvider TokenProvider { get; }
        internal MyFitnessPalConnectorService Service { get; }
        internal MyFitnessPalConnectorConfiguration Config { get; } = new()
        {
            Username = "user@example.com",
            Password = "secret",
            LookbackDays = 7,
        };

        internal List<ConnectorFoodEntryImport> Imported { get; } = [];

        internal InMemoryConnectorStateStore State { get; }

        /// <param name="publisher">
        ///     The publisher the service writes to; when null, one that records the imports in
        ///     <see cref="Imported"/> and keeps state in a fresh <see cref="State"/>.
        /// </param>
        /// <param name="source">MyFitnessPal's answers; when null, a <see cref="DiaryHandler"/> over <paramref name="entries"/>.</param>
        internal Fixture(
            TimeProvider clock,
            IReadOnlyList<(string Id, string Date)> entries,
            IConnectorPublisher? publisher = null,
            HttpMessageHandler? source = null)
        {
            var http = new HttpClient(source ?? new DiaryHandler(entries));
            var tenant = ResolvedTenant.Accessor();
            var resolver = new ConnectorServerResolver<MyFitnessPalConnectorConfiguration>(null, null, null);

            TokenProvider = new MyFitnessPalAuthTokenProvider(
                http,
                new ConnectorTokenCache(clock),
                resolver,
                tenant,
                NullLogger<MyFitnessPalAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                clock);

            var configService = new Mock<IConnectorConfigurationService>();
            configService
                .Setup(s => s.GetSecretsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

            var metadata = new Mock<IMetadataPublisher>();
            metadata
                .Setup(p => p.PublishConnectorFoodEntriesAsync(
                    It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(),
                    It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(),
                    It.IsAny<CancellationToken>()))
                .Callback((IEnumerable<ConnectorFoodEntryImport> batch, string _, WriteOrigin _, CancellationToken _) =>
                    Imported.AddRange(batch))
                .ReturnsAsync(new List<ConnectorFoodEntry>());

            State = publisher?.State as InMemoryConnectorStateStore ?? new InMemoryConnectorStateStore();
            var recording = new Mock<IConnectorPublisher>();
            recording.Setup(p => p.IsAvailable).Returns(true);
            recording.Setup(p => p.Metadata).Returns(metadata.Object);
            recording.Setup(p => p.State).Returns(State);

            Service = new MyFitnessPalConnectorService(
                http,
                resolver,
                NullLogger<MyFitnessPalConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                TokenProvider,
                configService.Object,
                publisher ?? recording.Object,
                clock);
        }
    }

    /// <param name="tokenAnswer">The token endpoint's status and body; when null, a token for user <c>u1</c>.</param>
    internal sealed class DiaryHandler(
        IReadOnlyList<(string Id, string Date)> entries,
        (HttpStatusCode Status, string Body)? tokenAnswer = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == MyFitnessPalConstants.Endpoints.Token)
                return Task.FromResult(tokenAnswer is { } answer
                    ? Json(answer.Body, answer.Status)
                    : Json("""{"access_token":"token","refresh_token":"refresh","expires_in":7200,"user_id":"u1"}"""));

            if (path == MyFitnessPalConstants.Endpoints.GraphQl)
                return Task.FromResult(Json(DiaryPage()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string DiaryPage()
        {
            var edges = string.Join(",", entries.Select(e => $$"""
                {
                  "node": {
                    "__typename": "ActiveFoodDiaryEntry",
                    "id": "{{e.Id}}",
                    "date": "{{e.Date}}",
                    "consumedAt": "12:00:00",
                    "quantity": 1
                  },
                  "syncEdgeInfo": { "operation": "UPSERT" }
                }
                """));

            return "{\"data\":{\"batchSync\":{\"foodDiaryEntries\":{\"edges\":[" + edges
                + "],\"pageInfo\":{\"hasPreviousPage\":false}}}}}";
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
