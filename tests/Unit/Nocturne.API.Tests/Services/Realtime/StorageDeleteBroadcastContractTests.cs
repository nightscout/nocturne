using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    private sealed class BroadcastCapture
    {
        public Mock<ISignalRBroadcastService> Broadcast { get; } = new();
        private object? _payload;

        public BroadcastCapture()
        {
            Broadcast
                .Setup(b => b.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()))
                .Callback<string, object>((_, data) => _payload = data)
                .Returns(Task.CompletedTask);
        }

        public JsonElement Payload
        {
            get
            {
                _payload.Should().NotBeNull("the producer must broadcast a delete event");
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(_payload));
            }
        }
    }

    private static void AssertSingleRecordShape(JsonElement payload, string collection, string sourceId)
    {
        payload.GetProperty("colName").GetString().Should().Be(collection);
        payload
            .GetProperty("identifier")
            .GetString()
            .Should()
            .Be(
                MongoObjectId.Coerce(sourceId),
                "clients match the identifier against the id the legacy wire gave them"
            );
    }

    private static WriteSideEffectsService CreateSideEffects(BroadcastCapture capture) =>
        new(
            Mock.Of<ICacheService>(),
            capture.Broadcast.Object,
            Mock.Of<IDecompositionPipeline>(),
            MockTenantAccessor.Create().Object,
            Enumerable.Empty<ICollectionEffectDescriptor>(),
            NullLogger<WriteSideEffectsService>.Instance
        );

    private static SignalRTreatmentEventSink CreateTreatmentSink(BroadcastCapture capture) =>
        new(capture.Broadcast.Object, NullLogger<SignalRTreatmentEventSink>.Instance);

    private static ActivityService CreateActivityService(
        BroadcastCapture capture,
        Mock<IStateSpanService> stateSpans,
        Mock<ISleepService> sleep
    ) =>
        new(
            stateSpans.Object,
            sleep.Object,
            Mock.Of<IDocumentProcessingService>(),
            capture.Broadcast.Object,
            Mock.Of<IDataEventSink<Activity>>(),
            Mock.Of<IActivityDecomposer>(),
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            NullLogger<ActivityService>.Instance
        );

    [Theory]
    [InlineData("entries")]
    [InlineData("devicestatus")]
    public async Task WriteSideEffects_SingleDelete_CarriesColNameAndIdentifier(string collection)
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture)
            .OnDeletedAsync(collection, new Entry { Id = RecordGuid, Sgv = 120 });

        AssertSingleRecordShape(capture.Payload, collection, RecordGuid);
        capture.Payload.GetProperty("doc").GetProperty("_id").GetString().Should().Be(RecordGuid);
    }

    [Fact]
    public async Task WriteSideEffects_BulkDelete_CarriesColNameAndCountButNoIdentifier()
    {
        var capture = new BroadcastCapture();

        await CreateSideEffects(capture).OnBulkDeletedAsync("entries", 17);

        var payload = capture.Payload;
        payload.GetProperty("colName").GetString().Should().Be("entries");
        payload.GetProperty("deletedCount").GetInt64().Should().Be(17);
        payload
            .TryGetProperty("identifier", out _)
            .Should()
            .BeFalse("a range delete materialises no ids; clients reconcile it on catch-up");
    }

    [Fact]
    public async Task TreatmentSink_Delete_CarriesColNameAndIdentifier()
    {
        var capture = new BroadcastCapture();

        await CreateTreatmentSink(capture)
            .OnDeletedAsync(new Treatment { Id = RecordGuid, EventType = "Meal Bolus" }, CancellationToken.None);

        AssertSingleRecordShape(capture.Payload, "treatments", RecordGuid);
    }

    /// <summary>
    /// The identifier on the wire has to be the same string the v3 REST projection served, or the
    /// client's lookup misses just as surely as it does on an empty one.
    /// </summary>
    [Fact]
    public async Task DeleteIdentifier_MatchesTheV3RestProjection()
    {
        var treatment = new Treatment { Id = RecordGuid, EventType = "Meal Bolus" };
        var entry = new Entry { Id = RecordGuid, Sgv = 120 };

        var restTreatmentId = RestIdentifier(treatment);
        var restEntryId = RestIdentifier(new EntryV3Response(entry));

        var treatmentCapture = new BroadcastCapture();
        await CreateTreatmentSink(treatmentCapture).OnDeletedAsync(treatment, CancellationToken.None);

        var entryCapture = new BroadcastCapture();
        await CreateSideEffects(entryCapture).OnDeletedAsync("entries", entry);

        treatmentCapture.Payload.GetProperty("identifier").GetString().Should().Be(restTreatmentId);
        entryCapture.Payload.GetProperty("identifier").GetString().Should().Be(restEntryId);

        static string? RestIdentifier(object projection) =>
            JsonSerializer
                .Deserialize<JsonElement>(JsonSerializer.Serialize(projection))
                .GetProperty("identifier")
                .GetString();
    }

    [Fact]
    public async Task SimpleEntityService_Delete_CarriesColNameAndIdentifier()
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
            capture.Broadcast.Object,
            NullLogger<HeartRateService>.Instance
        ).DeleteHeartRateAsync(RecordGuid);

        deleted.Should().BeTrue();
        AssertSingleRecordShape(capture.Payload, "heartrate", RecordGuid);
    }

    [Fact]
    public async Task ActivityService_SingleDelete_CarriesColNameAndIdentifier()
    {
        var stateSpans = new Mock<IStateSpanService>();
        stateSpans
            .Setup(s => s.DeleteActivityAsync(RecordGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capture = new BroadcastCapture();
        await CreateActivityService(capture, stateSpans, new Mock<ISleepService>())
            .DeleteActivityAsync(RecordGuid, CancellationToken.None);

        AssertSingleRecordShape(capture.Payload, "activity", RecordGuid);
    }

    [Fact]
    public async Task ActivityService_SleepDelete_CarriesColNameAndIdentifier()
    {
        var sleep = new Mock<ISleepService>();
        sleep
            .Setup(s => s.DeleteSessionAsync(Guid.Parse(RecordGuid), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capture = new BroadcastCapture();
        await CreateActivityService(capture, new Mock<IStateSpanService>(), sleep)
            .DeleteActivityAsync(RecordGuid, CancellationToken.None);

        AssertSingleRecordShape(capture.Payload, "activity", RecordGuid);
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

        var payload = capture.Payload;
        payload.GetProperty("colName").GetString().Should().Be("activity");
        payload.GetProperty("deletedCount").GetInt64().Should().Be(1);
    }
}
