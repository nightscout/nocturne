using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Effects;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Extensions;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Realtime;

/// <summary>
/// NS v3 socket clients route a storage <c>delete</c> on the event's top-level <c>colName</c> and
/// look the removed record up by its top-level <c>identifier</c> — AAPS's <c>onDataDelete</c> reads
/// both with <c>JSONObject.optString</c>, which yields <c>""</c> rather than null for a missing key,
/// so a payload carrying neither deletes nothing and reports no error. The reference Nightscout
/// server emits exactly those two keys.
/// </summary>
[Trait("Category", "Unit")]
public class StorageDeleteBroadcastContractTests
{
    private const string RecordGuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Runs the producers against a real <see cref="SignalRBroadcastService"/> and reads the events
    /// back as <see cref="JsonHubProtocol"/> writes them. Asserting on anything else — a fresh
    /// <see cref="JsonSerializer"/> call over the captured object, say — validates a serialization
    /// no client ever sees.
    /// </summary>
    private sealed class BroadcastCapture
    {
        private readonly JsonHubProtocolOptions _protocol = new();
        private readonly Dictionary<string, (string Group, object Data)> _sent = [];

        public ISignalRBroadcastService Broadcast { get; }

        /// <param name="configurePayloadSerializer">
        /// Reconfigures the serializer the hubs send payloads with, as an app calling
        /// <c>AddJsonProtocol</c> would.
        /// </param>
        public BroadcastCapture(Action<JsonSerializerOptions>? configurePayloadSerializer = null)
        {
            configurePayloadSerializer?.Invoke(_protocol.PayloadSerializerOptions);

            Broadcast = new SignalRBroadcastService(
                StubHub<DataHub>(),
                StubHub<AlarmHub>(),
                StubHub<ConfigHub>(),
                StubHub<AlertHub>(),
                StubHub<HomeAssistantHub>(),
                StubHub<OverviewHub>(),
                MockTenantAccessor.Create().Object,
                Options.Create(_protocol),
                NullLogger<SignalRBroadcastService>.Instance
            );
        }

        public JsonElement Delete => Wire("delete");

        public JsonElement Create => Wire("create");

        private IHubContext<THub> StubHub<THub>()
            where THub : Hub
        {
            var clients = new Mock<IHubClients>();
            clients
                .Setup(c => c.Group(It.IsAny<string>()))
                .Returns((string group) =>
                {
                    var proxy = new Mock<IClientProxy>();
                    proxy
                        .Setup(p =>
                            p.SendCoreAsync(
                                It.IsAny<string>(),
                                It.IsAny<object?[]>(),
                                It.IsAny<CancellationToken>()
                            )
                        )
                        .Callback<string, object?[], CancellationToken>(
                            (method, args, _) => _sent[method] = (group, args[0]!)
                        )
                        .Returns(Task.CompletedTask);
                    return proxy.Object;
                });

            var hub = new Mock<IHubContext<THub>>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            return hub.Object;
        }

        /// <summary>
        /// The event's bytes on the wire. The first argument of the hub invocation selects the
        /// SignalR tenant group; the bridge picks the socket.io room off the payload's
        /// <c>colName</c>. If the two disagree the event is delivered to a room no client of that
        /// collection has joined.
        /// </summary>
        private JsonElement Wire(string method)
        {
            _sent.ContainsKey(method).Should().BeTrue($"the producer must broadcast a {method} event");
            var (group, data) = _sent[method];

            var frame = new JsonHubProtocol(Options.Create(_protocol))
                .GetMessageBytes(new InvocationMessage(method, [data]))
                .ToArray();
            var payload = JsonSerializer
                .Deserialize<JsonElement>(frame.AsSpan(0, frame.Length - 1)) // trailing 0x1e separator
                .GetProperty("arguments")[0];

            group
                .Should()
                .Be(
                    TenantAwareHub.FormatTenantGroup(
                        MockTenantAccessor.DefaultTenantId.ToString(),
                        payload.GetProperty("colName").GetString()!
                    ),
                    "the SignalR group and the payload's colName select the same audience and must agree"
                );
            return payload;
        }
    }

    private static WriteSideEffectsService CreateSideEffects(BroadcastCapture capture) =>
        new(
            Mock.Of<ICacheService>(),
            capture.Broadcast,
            Mock.Of<IDecompositionPipeline>(),
            MockTenantAccessor.Create().Object,
            Enumerable.Empty<ICollectionEffectDescriptor>(),
            NullLogger<WriteSideEffectsService>.Instance
        );

    private static SignalRTreatmentEventSink CreateTreatmentSink(BroadcastCapture capture) =>
        new(capture.Broadcast, NullLogger<SignalRTreatmentEventSink>.Instance);

    private static ActivityService CreateActivityService(
        BroadcastCapture capture,
        Mock<IStateSpanService> stateSpans,
        Mock<ISleepService> sleep
    ) =>
        new(
            stateSpans.Object,
            sleep.Object,
            Mock.Of<IDocumentProcessingService>(),
            capture.Broadcast,
            Mock.Of<IDataEventSink<Activity>>(),
            Mock.Of<IActivityDecomposer>(),
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            NullLogger<ActivityService>.Instance
        );

    private static string? Identifier(JsonElement payload) =>
        payload.GetProperty("identifier").GetString();

    /// <summary>
    /// <see cref="Entry"/> carries an <c>identifier</c> of its own; <see cref="DeviceStatus"/> carries
    /// none, so its delete falls back to <c>_id</c>. Neither model coerces, so both send the uuid.
    /// </summary>
    public static TheoryData<string, object> LegacyRecords =>
        new()
        {
            { "entries", new Entry { Id = RecordGuid, Sgv = 120 } },
            { "devicestatus", new DeviceStatus { Id = RecordGuid } },
        };

    [Theory]
    [MemberData(nameof(LegacyRecords))]
    public async Task WriteSideEffects_SingleDelete_CarriesColNameAndIdentifier(
        string collection,
        object record
    )
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnDeletedAsync(collection, record);

        Identifier(capture.Delete).Should().Be(RecordGuid);
        capture.Delete.GetProperty("doc").GetProperty("_id").GetString().Should().Be(RecordGuid);
    }

    [Fact]
    public async Task WriteSideEffects_BulkDelete_CarriesColNameAndCountButNoIdentifier()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnBulkDeletedAsync("entries", 17);

        capture.Delete.GetProperty("deletedCount").GetInt64().Should().Be(17);
        capture
            .Delete.TryGetProperty("identifier", out _)
            .Should()
            .BeFalse("a range delete materialises no ids; clients reconcile it on catch-up");
    }

    [Fact]
    public async Task TreatmentSink_Delete_CarriesColNameIdentifierAndDoc()
    {
        var capture = new BroadcastCapture();

        await CreateTreatmentSink(capture)
            .OnDeletedAsync(new Treatment { Id = RecordGuid, EventType = "Meal Bolus" }, CancellationToken.None);

        Identifier(capture.Delete).Should().Be(MongoObjectId.Coerce(RecordGuid));
        capture
            .Delete.GetProperty("doc")
            .GetProperty("eventType")
            .GetString()
            .Should()
            .Be("Meal Bolus", "the web client reads the removed record out of doc");
    }

    /// <summary>
    /// The invariant the whole fix rests on: a client only ever holds the identifier its create
    /// event delivered, so a delete carrying any other spelling of the same id misses the lookup
    /// exactly as an empty one does.
    /// </summary>
    [Fact]
    public async Task DeleteIdentifier_EqualsTheCreateIdentifier_ForEntries()
    {
        var entry = new Entry { Id = RecordGuid, Sgv = 120 };
        var capture = new BroadcastCapture();
        var sideEffects = CreateSideEffects(capture);

        await sideEffects.OnCreatedAsync("entries", new[] { entry });
        await sideEffects.OnDeletedAsync("entries", entry);

        var doc = capture.Create.GetProperty("doc");
        doc.GetProperty("_id")
            .GetString()
            .Should()
            .Be(doc.GetProperty("identifier").GetString(), "one record cannot ship two ids");
        Identifier(capture.Delete).Should().Be(doc.GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task DeleteIdentifier_EqualsTheCreateIdentifier_ForTreatments()
    {
        var treatment = new Treatment { Id = RecordGuid, EventType = "Meal Bolus" };
        var capture = new BroadcastCapture();
        var sink = CreateTreatmentSink(capture);

        await sink.OnCreatedAsync(treatment, CancellationToken.None);
        await sink.OnDeletedAsync(treatment, CancellationToken.None);

        Identifier(capture.Delete)
            .Should()
            .Be(capture.Create.GetProperty("doc").GetProperty("identifier").GetString());
    }

    /// <summary>
    /// A treatment has one identifier everywhere, so a client that loaded it over REST resolves the
    /// delete too.
    /// </summary>
    [Fact]
    public async Task DeleteIdentifier_MatchesTheV3RestProjection_ForTreatments()
    {
        var treatment = new Treatment { Id = RecordGuid, EventType = "Meal Bolus" };

        var capture = new BroadcastCapture();
        await CreateTreatmentSink(capture).OnDeletedAsync(treatment, CancellationToken.None);

        Identifier(capture.Delete).Should().Be(RestIdentifier(treatment));
    }

    /// <summary>
    /// Entries do not have one identifier everywhere: the REST wrapper coerces its id to an ObjectId
    /// and the model the socket serializes does not. Delete follows the socket, because that is the
    /// half this event has to agree with, which leaves a client that only ever loaded the reading over
    /// REST unable to resolve the delete. Pinned so the divergence is deliberate and so this test
    /// fails the day the two are unified.
    /// </summary>
    [Fact]
    public async Task DeleteIdentifier_DivergesFromTheV3RestProjection_ForEntries()
    {
        var entry = new Entry { Id = RecordGuid, Sgv = 120 };

        var capture = new BroadcastCapture();
        await CreateSideEffects(capture).OnDeletedAsync("entries", entry);

        Identifier(capture.Delete).Should().Be(RecordGuid);
        RestIdentifier(new EntryV3Response(entry)).Should().Be(MongoObjectId.Coerce(RecordGuid));
    }

    private static string? RestIdentifier(object projection) =>
        JsonSerializer
            .Deserialize<JsonElement>(JsonSerializer.Serialize(projection))
            .GetProperty("identifier")
            .GetString();

    /// <summary>
    /// A record whose two id spellings disagree, to pin which one the delete follows. No shipping
    /// model does this today — <see cref="Treatment"/> coerces both, <see cref="Entry"/> neither —
    /// so the precedence is only observable here.
    /// </summary>
    private sealed class DisagreeingIds
    {
        /// <summary>A decoy: the first id-ish key on the wire is not the one clients match on.</summary>
        [JsonPropertyName("pumpId")]
        public string PumpId => "from-pump-id";

        [JsonPropertyName("identifier")]
        public string Identifier => "from-identifier";

        [JsonPropertyName("_id")]
        public string Id => "from-underscore-id";
    }

    [Fact]
    public async Task DeleteIdentifier_PrefersTheV3IdentifierOverTheLegacyId()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnDeletedAsync("entries", new DisagreeingIds());

        Identifier(capture.Delete).Should().Be("from-identifier");
    }

    /// <summary>
    /// A document that spells its id the ordinary C# way, as a projection or DTO added later would.
    /// Its wire spelling is whatever the hub's naming policy makes of it, which is the only spelling
    /// the derivation may look for.
    /// </summary>
    private sealed class UnattributedIdentifier
    {
        public string Identifier => RecordGuid;
    }

    [Fact]
    public async Task DeleteIdentifier_ReadsTheDocumentAsTheHubSpellsIt()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnDeletedAsync("entries", new UnattributedIdentifier());

        capture.Delete.GetProperty("doc").GetProperty("identifier").GetString().Should().Be(RecordGuid);
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    [Fact]
    public async Task DeleteIdentifier_FollowsAReconfiguredPayloadSerializer()
    {
        var capture = new BroadcastCapture(o => o.PropertyNamingPolicy = null);

        await CreateSideEffects(capture).OnDeletedAsync("entries", new UnattributedIdentifier());

        var doc = capture.Delete.GetProperty("doc");
        doc.TryGetProperty("identifier", out _)
            .Should()
            .BeFalse("this serializer spells the property verbatim");
        doc.GetProperty("Identifier").GetString().Should().Be(RecordGuid);
        capture
            .Delete.GetProperty("identifier")
            .ValueKind.Should()
            .Be(
                JsonValueKind.Null,
                "the document put no contract id on the wire, and an id no client can match is worth no more than none"
            );
    }

    /// <summary>
    /// The bulk keys are the same wire contract as the single-record ones, so a reconfigured
    /// payload serializer must not be able to rename them either.
    /// </summary>
    [Fact]
    public async Task BulkDeleteKeys_SurviveAReconfiguredPayloadSerializer()
    {
        var capture = new BroadcastCapture(o => o.PropertyNamingPolicy = null);

        await CreateSideEffects(capture).OnBulkDeletedAsync("entries", 17);

        capture.Delete.GetProperty("colName").GetString().Should().Be("entries");
        capture.Delete.GetProperty("deletedCount").GetInt64().Should().Be(17);
    }

    /// <summary>
    /// The identifier a client reads and the document id it would match against are the same string
    /// on the wire, under a serializer that is not the default.
    /// </summary>
    [Fact]
    public async Task WireIdentifier_MatchesTheDocumentIdOnTheWire()
    {
        var capture = new BroadcastCapture(o => o.PropertyNamingPolicy = null);

        await CreateSideEffects(capture).OnDeletedAsync("entries", new LegacyIdOnly());

        var doc = capture.Delete.GetProperty("doc");
        doc.ValueKind.Should().Be(JsonValueKind.Object);
        capture
            .Delete.GetProperty("identifier")
            .GetString()
            .Should()
            .Be(
                doc.GetProperty("_id").GetString(),
                "an identifier that does not match the document's own id resolves nothing"
            );
    }

    /// <summary>A document carrying only the legacy id, and no record behind it to fall back on.</summary>
    private sealed class LegacyIdOnly
    {
        [JsonPropertyName("_id")]
        public string Id => RecordGuid;
    }

    [Fact]
    public async Task DeleteIdentifier_FallsBackToTheLegacyId()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnDeletedAsync("entries", new LegacyIdOnly());

        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    /// <summary>A document whose id is a number, as a legacy import or an external store may spell it.</summary>
    private sealed class NumericId
    {
        [JsonPropertyName("_id")]
        public long Id => 12345;
    }

    [Fact]
    public async Task DeleteIdentifier_CarriesANumericIdAsTheStringClientsMatchOn()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnDeletedAsync("entries", new NumericId());

        capture.Delete.GetProperty("doc").GetProperty("_id").GetInt64().Should().Be(12345);
        Identifier(capture.Delete).Should().Be("12345");
    }

    /// <summary>A record whose document spells neither contract key, leaving only its own id.</summary>
    private sealed class OrdinaryIdDocument : IProcessableDocument
    {
        public string? Id { get; set; }
        public string? CreatedAt { get; set; }
        public long Mills { get; set; }
        public int? UtcOffset { get; set; }

        public Dictionary<string, string?> GetSanitizableFields() => [];

        public void SetSanitizedField(string fieldName, string? sanitizedValue) { }
    }

    [Fact]
    public async Task DeleteIdentifier_FallsBackToTheRecordId_WhenTheDocumentSpellsNeitherKey()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture)
            .OnDeletedAsync("entries", new OrdinaryIdDocument { Id = RecordGuid });

        var doc = capture.Delete.GetProperty("doc");
        doc.TryGetProperty("_id", out _).Should().BeFalse();
        doc.TryGetProperty("identifier", out _).Should().BeFalse();
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    /// <summary>
    /// The web app's realtime store matches a create against a delete on <c>doc._id</c> alone, and
    /// the entries it holds come from the V4 REST DTO, whose <c>_id</c> is the record's full uuid.
    /// A broadcast that shortens <c>_id</c> leaves a deleted reading on the chart until reload.
    /// </summary>
    [Fact]
    public async Task EntriesBroadcastDocId_StaysTheFullUuid()
    {
        var entry = new Entry { Id = RecordGuid, Sgv = 120 };
        var capture = new BroadcastCapture();
        var sideEffects = CreateSideEffects(capture);

        await sideEffects.OnCreatedAsync("entries", new[] { entry });
        await sideEffects.OnDeletedAsync("entries", entry);

        capture.Create.GetProperty("doc").GetProperty("_id").GetString().Should().Be(RecordGuid);
        capture.Delete.GetProperty("doc").GetProperty("_id").GetString().Should().Be(RecordGuid);
    }

    /// <summary>
    /// The Nocturne-native collections serve raw uuids on every surface they have, so coercing
    /// their delete identifier would emit an id none of those surfaces produces.
    /// </summary>
    [Fact]
    public async Task SimpleEntityService_Delete_SendsTheUuidVerbatim()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new NocturneDbContext(options) { TenantId = TenantId };
        context.HeartRates.Add(new HeartRateEntity
        {
            Id = Guid.Parse(RecordGuid),
            TenantId = TenantId,
            Timestamp = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
            Bpm = 60,
        });
        await context.SaveChangesAsync();

        var capture = new BroadcastCapture();
        var deleted = await new HeartRateService(
            context,
            Mock.Of<IDocumentProcessingService>(),
            capture.Broadcast,
            NullLogger<HeartRateService>.Instance
        ).DeleteHeartRateAsync(RecordGuid);

        deleted.Should().BeTrue();
        capture.Delete.GetProperty("colName").GetString().Should().Be("heartrate");
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    [Fact]
    public async Task ActivityService_SingleDelete_SendsTheUuidVerbatim()
    {
        var stateSpans = new Mock<IStateSpanService>();
        stateSpans
            .Setup(s => s.DeleteActivityAsync(RecordGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capture = new BroadcastCapture();
        await CreateActivityService(capture, stateSpans, new Mock<ISleepService>())
            .DeleteActivityAsync(RecordGuid, CancellationToken.None);

        capture.Delete.GetProperty("colName").GetString().Should().Be("activity");
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    [Fact]
    public async Task ActivityService_SleepDelete_SendsTheUuidVerbatim()
    {
        var sleep = new Mock<ISleepService>();
        sleep
            .Setup(s => s.DeleteSessionAsync(Guid.Parse(RecordGuid), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capture = new BroadcastCapture();
        await CreateActivityService(capture, new Mock<IStateSpanService>(), sleep)
            .DeleteActivityAsync(RecordGuid, CancellationToken.None);

        capture.Delete.GetProperty("colName").GetString().Should().Be("activity");
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    [Fact]
    public async Task ActivityService_BulkDelete_CarriesColNameAndCount()
    {
        var stateSpans = new Mock<IStateSpanService>();
        stateSpans
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Activity { Id = RecordGuid, Type = "exercise", Mills = 1 }]);
        stateSpans
            .Setup(s => s.DeleteActivityAsync(RecordGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capture = new BroadcastCapture();
        await CreateActivityService(capture, stateSpans, new Mock<ISleepService>())
            .DeleteMultipleActivitiesAsync("exercise", CancellationToken.None);

        capture.Delete.GetProperty("colName").GetString().Should().Be("activity");
        capture.Delete.GetProperty("deletedCount").GetInt64().Should().Be(1);
    }
}
