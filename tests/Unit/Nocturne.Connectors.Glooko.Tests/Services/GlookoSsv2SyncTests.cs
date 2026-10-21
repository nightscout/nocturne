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

        result.Success.Should().BeFalse("a skipped feed is a failed fetch, as it is on the windowed path");
        result.Errors.Should().Contain("Failed to fetch Boluses");
        publishedGlucose.Should().HaveCount(2, "glucose still imports despite the bolus feed failing");
        handler.RequestedPaths.Should().Contain(p => p.Contains("/api/v2/foods"),
            "resources after the failing one must still be fetched");
    }

    [Fact]
    public async Task Ssv2_FeedTimesOutOnEveryAttempt_IsReportedAndKeepsItsCursorAndTheRestOfThePass()
    {
        var publishedGlucose = new List<SensorGlucose>();
        var cursorStore = new FakeCursorStore();
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/cgm/egvs"))
                return EgvPage(["e1", "e2"], "2026-06-03T00:00:00.000Z", "g1", lastPage: true);
            if (path.Contains("/api/v2/notes"))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
            return Json("{\"lastPage\":true}");
        });

        var result = await BuildService(handler, cursorStore, CapturePublisher(publishedGlucose))
            .SyncDataAsync(new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Notes] },
                Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Failed to fetch Notes");
        publishedGlucose.Should().HaveCount(2);
        cursorStore.Saved.Should().ContainKey("/api/v2/cgm/egvs");
        cursorStore.Saved.Should().NotContainKey("/api/v2/notes");
    }

    [Fact]
    public async Task Ssv2_InjectionBasalsFeedFails_WhileOnlyBolusesRequested_IsNotReported()
    {
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/pumps/injection_basals"))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
            return Json("{\"lastPage\":true}");
        });

        var result = await BuildService(handler, new FakeCursorStore(), CapturePublisher(new List<SensorGlucose>()))
            .SyncDataAsync(new SyncRequest { DataTypes = [SyncDataType.Boluses] }, Config(), CancellationToken.None);

        handler.RequestedPaths.Should().Contain(p => p.Contains("/api/v2/pumps/injection_basals"),
            "the pen basal feed is fetched whenever Boluses or BasalInjections is on");
        result.Success.Should().BeTrue("a feed whose records belong to an inactive type must not fail the sync");
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ssv2_WhenPublishDoesNotLand_KeepsTheCursorAndRefetchesTheSameRecords(bool publishThrows)
    {
        var cursorStore = new FakeCursorStore();
        var handler = new RoutingHandler(path =>
            path.Contains("/api/v2/cgm/egvs")
                ? EgvPage(["e1", "e2"], "2026-06-03T00:00:00.000Z", "g1", lastPage: true)
                : Json("{\"lastPage\":true}"));

        var failing = new Mock<IConnectorPublisher>();
        failing.Setup(p => p.IsAvailable).Returns(true);
        var glucosePublish = failing.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
            It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
            It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()));
        if (publishThrows)
            glucosePublish.ThrowsAsync(new InvalidOperationException("publish failed"));
        else
            glucosePublish.ReturnsAsync(false);

        var firstRun = await BuildService(handler, cursorStore, failing)
            .SyncDataAsync(GlucoseRequest(), Config(), CancellationToken.None);

        firstRun.Success.Should().BeFalse();
        cursorStore.Saved.Should().NotContainKey("/api/v2/cgm/egvs",
            "records that were fetched but never published must not be skipped by the next sync");

        var publishedGlucose = new List<SensorGlucose>();
        var secondRun = await BuildService(handler, cursorStore, CapturePublisher(publishedGlucose))
            .SyncDataAsync(GlucoseRequest(), Config(), CancellationToken.None);

        secondRun.Success.Should().BeTrue();
        handler.EgvsCursors.Should().Equal(Epoch, Epoch);
        publishedGlucose.Should().HaveCount(2);
        cursorStore.Saved.Should().ContainKey("/api/v2/cgm/egvs")
            .WhoseValue.Should().BeEquivalentTo(new ConnectorSyncCursor("2026-06-03T00:00:00.000Z", "g1"));
    }

    [Fact]
    public async Task Ssv2_OneResourcePublishFailing_DoesNotHoldBackAnotherResourcesCursor()
    {
        var cursorStore = new FakeCursorStore();
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/cgm/egvs"))
                return EgvPage(["e1"], "2026-06-03T00:00:00.000Z", "g1", lastPage: true);
            if (path.Contains("/api/v2/notes"))
                return Json("{\"notes\":[{\"timestamp\":\"2026-06-01T08:00:00.000Z\",\"value\":\"n\",\"guid\":\"n1\"}],"
                            + "\"lastPage\":true,\"lastUpdatedAt\":\"2026-06-04T00:00:00.000Z\",\"lastGuid\":\"n1\"}");
            return Json("{\"lastPage\":true}");
        });

        var publisher = new Mock<IConnectorPublisher> { DefaultValue = DefaultValue.Mock };
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        publisher.Setup(p => p.Metadata.PublishNotesAsync(
                It.IsAny<IEnumerable<Note>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await BuildService(handler, cursorStore, publisher).SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose, SyncDataType.Notes] },
            Config(), CancellationToken.None);

        cursorStore.Saved.Should().NotContainKey("/api/v2/cgm/egvs");
        cursorStore.Saved.Should().ContainKey("/api/v2/notes")
            .WhoseValue.Should().BeEquivalentTo(new ConnectorSyncCursor("2026-06-04T00:00:00.000Z", "n1"));
    }

    [Fact]
    public async Task Ssv2_CursorPendingFromARejectedPass_IsNotCommittedByTheForbiddenRetry()
    {
        var cursorStore = new FakeCursorStore();
        var egvsRequests = 0;
        var bolusRequests = 0;
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/cgm/egvs"))
                return ++egvsRequests == 1
                    ? EgvPage(["e1"], "2026-06-03T00:00:00.000Z", "g1", lastPage: true)
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (path.Contains("/api/v2/pumps/normal_boluses") && ++bolusRequests == 1)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return Json("{\"lastPage\":true}");
        });

        var publisher = new Mock<IConnectorPublisher> { DefaultValue = DefaultValue.Mock };
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await BuildService(handler, cursorStore, publisher)
            .SyncDataAsync(GlucoseRequest(), Config(), CancellationToken.None);

        egvsRequests.Should().BeGreaterThan(1, "the forbidden response must have forced a second pass");
        cursorStore.Saved.Should().NotContainKey("/api/v2/cgm/egvs",
            "a cursor whose records the first pass failed to publish must not be committed by the retry");
    }

    [Fact]
    public async Task Ssv2_BatchFeeds_StoreTheirCursorsOnlyWhenEveryBatchPublishLands()
    {
        var cursorStore = new FakeCursorStore();
        var handler = new RoutingHandler(path =>
        {
            if (path.Contains("/api/v2/pumps/normal_boluses"))
                return Json("{\"normalBoluses\":[{\"pumpTimestamp\":\"2026-06-01T08:00:00.000Z\",\"timestamp\":\"2026-06-01T08:00:00.000Z\","
                            + "\"insulinDelivered\":2.0,\"deliveredUnits\":2.0,\"programmedUnits\":2.0,\"carbsInput\":0}],"
                            + "\"lastPage\":true,\"lastUpdatedAt\":\"2026-06-05T00:00:00.000Z\",\"lastGuid\":\"b1\"}");
            if (path.Contains("/api/v2/readings"))
                return Json("{\"readings\":[{\"timestamp\":\"2026-06-01T09:00:00.000Z\",\"value\":10000,\"guid\":\"r1\"}],"
                            + "\"lastPage\":true,\"lastUpdatedAt\":\"2026-06-06T00:00:00.000Z\",\"lastGuid\":\"r1\"}");
            return Json("{\"lastPage\":true}");
        });
        var request = new SyncRequest { DataTypes = [SyncDataType.Boluses, SyncDataType.ManualBG] };

        var rejecting = BatchPublisher(manualBgLands: false);
        await BuildService(handler, cursorStore, rejecting).SyncDataAsync(request, Config(), CancellationToken.None);

        cursorStore.Saved.Should().NotContainKey("/api/v2/pumps/normal_boluses");
        cursorStore.Saved.Should().NotContainKey("/api/v2/readings",
            "one rejected publish in the batch holds back every feed that fed it");

        await BuildService(handler, cursorStore, BatchPublisher(manualBgLands: true))
            .SyncDataAsync(request, Config(), CancellationToken.None);

        cursorStore.Saved.Should().ContainKey("/api/v2/pumps/normal_boluses")
            .WhoseValue.Should().BeEquivalentTo(new ConnectorSyncCursor("2026-06-05T00:00:00.000Z", "b1"));
        cursorStore.Saved.Should().ContainKey("/api/v2/readings")
            .WhoseValue.Should().BeEquivalentTo(new ConnectorSyncCursor("2026-06-06T00:00:00.000Z", "r1"));
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

    /// <summary>
    /// SSV2 resumes every resource from its own cursor, so the scheduled run is not put on the
    /// windowed path's walk schedule: the cursor store holds only the resource cursors afterwards.
    /// </summary>
    [Fact]
    public async Task Ssv2_ScheduledRun_IsNotPutOnTheFullWalkSchedule()
    {
        var store = new FakeCursorStore();
        var handler = new RoutingHandler(path =>
            path.Contains("/api/v2/cgm/egvs") ? EgvPage(["g1"], "2026-06-02T00:00:00Z", "g1", lastPage: true)
            : Json("{\"lastPage\":true}"));
        var service = BuildService(handler, store, CapturePublisher([]));

        await service.SyncDataAsync(Config(), CancellationToken.None);

        store.Saved.Keys.Should().NotContain(GlookoConstants.FullWalkCursorResource);
        store.Saved.Keys.Should().NotContain(GlookoConstants.FullWalkAttemptCursorResource);
    }

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

    private static Mock<IConnectorPublisher> BatchPublisher(bool manualBgLands)
    {
        var publisher = new Mock<IConnectorPublisher> { DefaultValue = DefaultValue.Mock };
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Treatments.PublishBolusesAsync(
                It.IsAny<IEnumerable<Bolus>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        publisher.Setup(p => p.Treatments.PublishBGChecksAsync(
                It.IsAny<IEnumerable<BGCheck>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(manualBgLands);
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
