using Nocturne.Tests.Shared.Infrastructure;
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
using Nocturne.Infrastructure.Data.Repositories.Health;
using V3DeviceStatusController = Nocturne.API.Controllers.V3.DeviceStatusController;
using Nocturne.API.Tests.TestDoubles;
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
using Nocturne.API.Services.Caching;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;
using static Nocturne.API.Tests.TestDoubles.StorageBroadcastCapture;

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

    private static Task PublishLiveAsync(StorageBroadcastCapture capture, RecordsChanged batch) =>
        new LegacyStorageRecordChangeSubscriber(capture.Broadcast).OnChangedAsync(batch, CancellationToken.None);

    /// <summary>
    /// A delete of a document no producer sends today, built as every producer builds one, to pin how
    /// <see cref="StorageDeleteEvent"/> reads the identifier off the document.
    /// </summary>
    private static Task SendDeleteAsync(StorageBroadcastCapture capture, object document) =>
        capture.Broadcast.BroadcastStorageDeleteAsync(
            "entries", new StorageDeleteEvent("entries", (document as IProcessableDocument)?.Id, document));

    private static SignalRTreatmentEventSink CreateTreatmentSink(StorageBroadcastCapture capture) =>
        new(capture.Broadcast);

    private static ActivityService CreateActivityService(
        StorageBroadcastCapture capture,
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

    /// <summary>
    /// A <see cref="DeviceStatus"/> carries no <c>identifier</c>, so its delete falls back to its
    /// <c>_id</c>, which is coerced to the ObjectId form.
    /// </summary>
    [Fact]
    public async Task DeviceStatusDelete_CarriesColNameAndIdentifier()
    {
        var capture = new StorageBroadcastCapture();

        await PublishLiveAsync(capture, RecordsChanged.Of<DeviceStatus>(WriteOrigin.Live, deleted: [new DeviceStatus { Id = RecordGuid }]));

        capture.Delete.GetProperty("colName").GetString().Should().Be("devicestatus");

        Identifier(capture.Delete).Should().Be(MongoObjectId.Coerce(RecordGuid));
        capture
            .Delete.GetProperty("doc")
            .GetProperty("_id")
            .GetString()
            .Should()
            .Be(MongoObjectId.Coerce(RecordGuid));
    }

    [Fact]
    public async Task TreatmentSink_Delete_CarriesColNameIdentifierAndDoc()
    {
        var capture = new StorageBroadcastCapture();

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

    [Fact]
    public async Task DeleteIdentifier_EqualsTheCreateIdentifier_ForTreatments()
    {
        var treatment = new Treatment { Id = RecordGuid, EventType = "Meal Bolus" };
        var capture = new StorageBroadcastCapture();
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

        var capture = new StorageBroadcastCapture();
        await CreateTreatmentSink(capture).OnDeletedAsync(treatment, CancellationToken.None);

        Identifier(capture.Delete).Should().Be(RestIdentifier(treatment));
    }

    [Fact]
    public async Task CreateIds_MatchTheRestProjections_ForDeviceStatus()
    {
        var status = new DeviceStatus { Id = RecordGuid, Device = "openaps://phone" };
        var capture = new StorageBroadcastCapture();

        await PublishLiveAsync(capture, RecordsChanged.Of<DeviceStatus>(WriteOrigin.Live, created: [status]));

        var v1 = Rest(status).GetProperty("_id").GetString();
        var v3 = RestIdentifier(V3DeviceStatusController.ToDocument(status));
        v1.Should().Be(v3);
        IdKeys(capture.Create.GetProperty("doc"))
            .Should()
            .BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = v3 });
    }

    /// <summary>
    /// An AAPS devicestatus upload whose loop has not run yet. AAPS reads the echo with Gson, which
    /// throws on a <c>null</c> where its model declares an object (<c>openaps.suggested</c>,
    /// <c>openaps.enacted</c>), so the keys the upload left out must stay out.
    /// </summary>
    private static DeviceStatus AapsUploadWithEmptyOpenAps() =>
        JsonSerializer.Deserialize<DeviceStatus>(
            """{"device":"openaps://samsung SM-S911B","created_at":"2026-10-23T09:47:00.000Z","openaps":{},"pump":{"reservoir":120}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

    /// <summary>
    /// Scalars a model writes even when null, as Nightscout does; Gson reads a null scalar without
    /// complaint.
    /// </summary>
    private static readonly string[] DeviceStatusAlwaysWritten = ["$.uploaderBattery"];

    [Fact]
    public async Task DeviceStatusCreate_LeavesOutTheOpenApsKeysTheUploadLeftOut()
    {
        var capture = new StorageBroadcastCapture();

        await PublishLiveAsync(capture, RecordsChanged.Of<DeviceStatus>(WriteOrigin.Live, created: [AapsUploadWithEmptyOpenAps()]));

        var doc = capture.Create.GetProperty("doc");
        doc.GetProperty("openaps").EnumerateObject().Should().BeEmpty();
        doc.GetProperty("pump").GetProperty("reservoir").GetDouble().Should().Be(120);
        NullProperties(doc).Should().Equal(DeviceStatusAlwaysWritten);
    }

    [Fact]
    public async Task DeviceStatusUpdate_LeavesOutTheOpenApsKeysTheUploadLeftOut()
    {
        var capture = new StorageBroadcastCapture();

        await PublishLiveAsync(capture, RecordsChanged.Of<DeviceStatus>(WriteOrigin.Live, updated: [AapsUploadWithEmptyOpenAps()]));

        capture.Update.GetProperty("doc").GetProperty("openaps").EnumerateObject().Should().BeEmpty();
        NullProperties(capture.Update.GetProperty("doc")).Should().Equal(DeviceStatusAlwaysWritten);
    }

    [Fact]
    public async Task TreatmentSinkCreate_LeavesOutUnsetKeys()
    {
        var capture = new StorageBroadcastCapture();

        await CreateTreatmentSink(capture)
            .OnCreatedAsync(new Treatment { Id = RecordGuid, EventType = "Meal Bolus", Carbs = 30 }, CancellationToken.None);

        var doc = capture.Create.GetProperty("doc");
        doc.GetProperty("carbs").GetDouble().Should().Be(30);
        NullProperties(doc).Should().Equal("$.enteredBy");
    }

    /// <summary>The paths of every object property that is on the wire as <c>null</c>.</summary>
    private static List<string> NullProperties(JsonElement element, string path = "$")
    {
        var found = new List<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var child = $"{path}.{property.Name}";
                if (property.Value.ValueKind == JsonValueKind.Null)
                    found.Add(child);
                else
                    found.AddRange(NullProperties(property.Value, child));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                found.AddRange(NullProperties(item, $"{path}[{index++}]"));
        }

        return found;
    }

    /// <summary>
    /// A record whose two id spellings disagree, to pin which one the delete follows. No shipping
    /// model does this today — <see cref="Treatment"/> and <see cref="Entry"/> coerce both — so the
    /// precedence is only observable here.
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
        var capture = new StorageBroadcastCapture();

        await SendDeleteAsync(capture, new DisagreeingIds());

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
        var capture = new StorageBroadcastCapture();

        await SendDeleteAsync(capture, new UnattributedIdentifier());

        capture.Delete.GetProperty("doc").GetProperty("identifier").GetString().Should().Be(RecordGuid);
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    [Fact]
    public async Task DeleteIdentifier_FollowsAReconfiguredPayloadSerializer()
    {
        var capture = new StorageBroadcastCapture(o => o.PropertyNamingPolicy = null);

        await SendDeleteAsync(capture, new UnattributedIdentifier());

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
    /// The identifier a client reads and the document id it would match against are the same string
    /// on the wire, under a serializer that is not the default.
    /// </summary>
    [Fact]
    public async Task WireIdentifier_MatchesTheDocumentIdOnTheWire()
    {
        var capture = new StorageBroadcastCapture(o => o.PropertyNamingPolicy = null);

        await SendDeleteAsync(capture, new LegacyIdOnly());

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
        var capture = new StorageBroadcastCapture();

        await SendDeleteAsync(capture, new LegacyIdOnly());

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
        var capture = new StorageBroadcastCapture();

        await SendDeleteAsync(capture, new NumericId());

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
        var capture = new StorageBroadcastCapture();

        await SendDeleteAsync(capture, new OrdinaryIdDocument { Id = RecordGuid });

        var doc = capture.Delete.GetProperty("doc");
        doc.TryGetProperty("_id", out _).Should().BeFalse();
        doc.TryGetProperty("identifier", out _).Should().BeFalse();
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }

    /// <summary>
    /// The Nocturne-native collections serve raw uuids on every surface they have, so coercing
    /// their delete identifier would emit an id none of those surfaces produces.
    /// </summary>
    [Fact]
    public async Task HealthSeries_Delete_SendsTheUuidVerbatim()
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

        var capture = new StorageBroadcastCapture();
        var deleted = await new HeartRateService(
            new HeartRateRepository(context.AsFactory(), TimeProvider.System),
            Mock.Of<IDocumentProcessingService>(),
            capture.Broadcast
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
            .Setup(s => s.GetActivityByIdAsync(RecordGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Activity { Id = RecordGuid, Type = "exercise" });
        stateSpans
            .Setup(s => s.DeleteActivityAsync(RecordGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[RecordGuid]);

        var capture = new StorageBroadcastCapture();
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
            .Setup(s => s.GetSessionByIdAsync(Guid.Parse(RecordGuid), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SleepSession { Id = RecordGuid, Source = SleepSource.Manual });
        sleep
            .Setup(s => s.DeleteSessionAsync(Guid.Parse(RecordGuid), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capture = new StorageBroadcastCapture();
        await CreateActivityService(capture, new Mock<IStateSpanService>(), sleep)
            .DeleteActivityAsync(RecordGuid, CancellationToken.None);

        capture.Delete.GetProperty("colName").GetString().Should().Be("activity");
        Identifier(capture.Delete).Should().Be(RecordGuid);
    }
}
