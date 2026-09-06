using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.V4;
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
/// Exercises the SSV2 cursor-pagination loop and per-resource isolation through the public
/// <see cref="GlookoConnectorService.SyncDataAsync"/> entry point (with <c>UseSsv2Sync</c> on).
/// </summary>
public class GlookoSsv2SyncTests
{
    private const string Epoch = "1970-01-01T00:00:00.000Z";
    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    [Fact]
    public async Task Ssv2_PaginatesUntilLastPage_AndPersistsFinalCursor()
    {
        var publishedGlucose = new List<SensorGlucose>();
        var cursorStore = new FakeCursorStore();
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/cgm/egvs"))
            {
                var cursor = QueryValue(path, "lastUpdatedAt");
                return cursor switch
                {
                    Epoch => EgvPage(["e1", "e2"], "2026-06-01T00:00:00.000Z", "g1", lastPage: false),
                    "2026-06-01T00:00:00.000Z" => EgvPage(["e3", "e4"], "2026-06-02T00:00:00.000Z", "g2", lastPage: false),
                    "2026-06-02T00:00:00.000Z" => EgvPage(["e5"], "2026-06-03T00:00:00.000Z", "g3", lastPage: true),
                    _ => Json("{\"egvs\":[],\"lastPage\":true}"),
                };
            }
            return Json("{\"lastPage\":true}"); // all other resources: empty single page
        });

        var service = BuildService(handler, cursorStore, CapturePublisher(publishedGlucose));
        var result = await service.SyncDataAsync(GlucoseRequest(), Config(), CancellationToken.None);

        result.Success.Should().BeTrue();
        // Three pages requested, each resuming from the prior page's cursor.
        handler.EgvsCursors.Should().Equal(Epoch, "2026-06-01T00:00:00.000Z", "2026-06-02T00:00:00.000Z");
        publishedGlucose.Should().HaveCount(5, "all records across all pages should be ingested");
        // Final advanced cursor persisted exactly once.
        cursorStore.Saved.Should().ContainKey("/api/v2/cgm/egvs")
            .WhoseValue.Should().BeEquivalentTo(new ConnectorSyncCursor("2026-06-03T00:00:00.000Z", "g3"));
    }

    [Fact]
    public async Task Ssv2_WhenCursorDoesNotAdvance_StopsInsteadOfLooping()
    {
        var cursorStore = new FakeCursorStore();
        // Always returns a full, non-last page whose cursor equals the request cursor → must not loop.
        var handler = new RoutingHandler(path =>
            path.Contains("/api/v2/cgm/egvs")
                ? EgvPage(["e1"], Epoch, ZeroGuid, lastPage: false)
                : Json("{\"lastPage\":true}"));

        var service = BuildService(handler, cursorStore, CapturePublisher(new List<SensorGlucose>()));
        var result = await service.SyncDataAsync(GlucoseRequest(), Config(), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.EgvsCursors.Should().ContainSingle("the loop guard must stop after one non-advancing page");
        cursorStore.Saved.Should().NotContainKey("/api/v2/cgm/egvs", "a cursor that never moved must not be persisted");
    }

    [Fact]
    public async Task Ssv2_OneResourceFailing_DoesNotAbortTheRest()
    {
        var publishedGlucose = new List<SensorGlucose>();
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/cgm/egvs"))
                return EgvPage(["e1", "e2"], "2026-06-03T00:00:00.000Z", "g1", lastPage: true);
            if (path.Contains("/api/v2/pumps/normal_boluses"))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError); // one feed is broken
            return Json("{\"lastPage\":true}");
        });

        var service = BuildService(handler, new FakeCursorStore(), CapturePublisher(publishedGlucose));
        var result = await service.SyncDataAsync(AllTypesRequest(), Config(), CancellationToken.None);

        result.Success.Should().BeTrue("a single failing feed must degrade, not abort the whole sync");
        publishedGlucose.Should().HaveCount(2, "glucose still imports despite the bolus feed failing");
        handler.RequestedPaths.Should().Contain(p => p.Contains("/api/v2/foods"),
            "resources after the failing one must still be fetched");
    }

    // Regression guard for the SupportedDataTypes gap: Notes/Activity (and the health feeds gated
    // under Activity) were absent from SupportedDataTypes, so activeTypes could never contain them and
    // the feeds were unreachable dead code. These assert the feeds are actually fetched when requested.

    [Fact]
    public async Task Ssv2_NotesRequest_FetchesNotesFeed()
    {
        var handler = new RoutingHandler(_ => Json("{\"lastPage\":true}"));
        var service = BuildService(handler, new FakeCursorStore(), CapturePublisher(new List<SensorGlucose>()));

        await service.SyncDataAsync(new SyncRequest { DataTypes = [SyncDataType.Notes] }, Config(), CancellationToken.None);

        handler.RequestedPaths.Should().Contain(p => p.Contains("/api/v2/notes"),
            "Notes must be reachable (it must be in SupportedDataTypes)");
    }

    [Fact]
    public async Task Ssv2_ActivityRequest_FetchesExerciseAndStepFeeds()
    {
        var handler = new RoutingHandler(_ => Json("{\"lastPage\":true}"));
        var service = BuildService(handler, new FakeCursorStore(), CapturePublisher(new List<SensorGlucose>()));

        await service.SyncDataAsync(new SyncRequest { DataTypes = [SyncDataType.Activity] }, Config(), CancellationToken.None);

        handler.RequestedPaths.Should().Contain(p => p.Contains("/api/v2/exercises"));
        handler.RequestedPaths.Should().Contain(p => p.Contains("/api/v2/validic/routines"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SyncRequest GlucoseRequest() => new() { DataTypes = [SyncDataType.Glucose] };
    private static SyncRequest AllTypesRequest() => new()
    {
        DataTypes = [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake],
    };

    private static GlookoConnectorConfiguration Config() => new()
    {
        ConnectSource = ConnectSource.Glooko,
        Email = "user@example.com",
        Password = "secret",
        Server = GlookoConstants.RegionEU,
        UseSsv2Sync = true,
        SyncGlucose = true,
        SyncBoluses = true,
        SyncCarbIntake = true,
    };

    private static GlookoConnectorService BuildService(
        RoutingHandler handler, IConnectorSyncCursorStore cursorStore, Mock<IConnectorPublisher> publisher) =>
        new(
            new HttpClient(handler),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new FixedGlookoTokenProvider(),
            publisher.Object,
            mealMatchingService: null,
            timezoneTimelineService: null,
            cursorStore: cursorStore);

    private static Mock<IConnectorPublisher> CapturePublisher(List<SensorGlucose> sink)
    {
        var publisher = new Mock<IConnectorPublisher>();
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, string, WriteOrigin, CancellationToken>(
                (g, _, _, _) => sink.AddRange(g))
            .ReturnsAsync(true);
        return publisher;
    }

    private static HttpResponseMessage EgvPage(string[] guids, string nextCursor, string nextGuid, bool lastPage)
    {
        var egvs = string.Join(",", guids.Select((g, i) =>
            $"{{\"displayTime\":\"2026-06-0{i + 1}T08:00:00.000Z\",\"glucoseValue\":10000,\"guid\":\"{g}\",\"calculated\":false,\"softDeleted\":false}}"));
        var lp = lastPage ? "true" : "false";
        return Json($"{{\"egvs\":[{egvs}],\"lastPage\":{lp},\"lastUpdatedAt\":\"{nextCursor}\",\"lastGuid\":\"{nextGuid}\"}}");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string QueryValue(string pathAndQuery, string key)
    {
        var marker = key + "=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += marker.Length;
        var end = pathAndQuery.IndexOf('&', start);
        return end < 0 ? pathAndQuery[start..] : pathAndQuery[start..end];
    }

    private sealed class RoutingHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];
        public List<string> EgvsCursors { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            RequestedPaths.Add(path);
            if (path.Contains("/api/v2/cgm/egvs"))
                EgvsCursors.Add(QueryValue(path, "lastUpdatedAt"));
            return Task.FromResult(route(path));
        }
    }

    private sealed class FakeCursorStore : IConnectorSyncCursorStore
    {
        public Dictionary<string, ConnectorSyncCursor> Saved { get; } = new(StringComparer.Ordinal);

        public Task<ConnectorSyncCursor?> GetAsync(string connectorName, string resource, CancellationToken ct = default)
            => Task.FromResult(Saved.TryGetValue(resource, out var c) ? c : null);

        public Task SetAsync(string connectorName, string resource, ConnectorSyncCursor cursor, CancellationToken ct = default)
        {
            Saved[resource] = cursor;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedGlookoTokenProvider : GlookoAuthTokenProvider
    {
        public FixedGlookoTokenProvider()
            : base(new HttpClient(), new ConnectorTokenCache(),
                   new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
                   new FakeTenantAccessor(), NullLogger<GlookoAuthTokenProvider>.Instance,
                   Mock.Of<IRetryDelayStrategy>()) { }

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            GlookoConnectorConfiguration config, CancellationToken cancellationToken)
        {
            var userData = JsonSerializer.Serialize(
                new GlookoUserData { User = new GlookoUserLogin { GlookoCode = "eu-test-code" } });
            return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                ("_logbook-web_session=sess", DateTime.UtcNow.AddHours(1),
                 new Dictionary<string, string> { ["SessionCookie"] = "_logbook-web_session=sess", ["UserData"] = userData }));
        }

        private sealed class FakeTenantAccessor : Nocturne.Core.Contracts.Multitenancy.ITenantAccessor
        {
            public bool IsResolved => true;
            public Guid TenantId => Guid.Empty;
            public Nocturne.Core.Contracts.Multitenancy.TenantContext? Context => null;
            public void SetTenant(Nocturne.Core.Contracts.Multitenancy.TenantContext context) { }
        }
    }
}
