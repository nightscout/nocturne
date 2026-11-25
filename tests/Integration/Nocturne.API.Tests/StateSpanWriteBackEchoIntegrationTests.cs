using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment stored as a state span (an override or a temporary target) or beside one (a pump
/// suspend), written back through <see cref="NightscoutTreatmentWriteBackSink"/> to a model of
/// Nightscout (<see cref="FakeNightscoutTreatments"/>), and pulled back as the Nightscout connector
/// pulls it, through the connector publisher. 15.0.7 and later keep the copy under a <c>_id</c> of
/// their own, with the key write-back sent as its <c>identifier</c>; up to 15.0.6 the copy is held
/// under the key itself. Every pull runs over a treatment service that writes back to the same
/// upstream, and must send it nothing.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class StateSpanWriteBackEchoIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string UpstreamUrl = "https://nightscout.example.com";

    private const string Override = "Temporary Override";
    private const string TemporaryTarget = "Temporary Target";
    private const string PumpSuspend = "Pump Suspend";

    /// <summary>A slot no other test writes into, since the fixture's tenant is shared, and years old, past every live window.</summary>
    private static DateTimeOffset UniqueSlot() =>
        new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 150_000) * 20);

    private static string At(DateTimeOffset slot) => slot.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private AsyncServiceScope OpenTenantScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:state-span-write-back");

    /// <summary>
    /// Runs <paramref name="act"/> on the tenant's treatment service and a connector publisher over it,
    /// writing back to <paramref name="upstream"/>.
    /// </summary>
    private async Task<T> WithWriteBackAsync<T>(
        HttpMessageHandler upstream, Func<ITreatmentService, ITreatmentPublisher, Task<T>> act)
    {
        await using var scope = OpenTenantScope();

        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NightscoutConnectorConfiguration
        {
            Url = UpstreamUrl,
            ApiSecret = "integration-secret-0001",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50,
        });
        var sink = new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream, disposeHandler: false),
            loader.Object,
            new NightscoutCircuitBreaker(),
            scope.ServiceProvider.GetRequiredService<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        var service = ActivatorUtilities.CreateInstance<TreatmentService>(scope.ServiceProvider, (IDataEventSink<Treatment>)sink);
        var publisher = ActivatorUtilities.CreateInstance<TreatmentPublisher>(scope.ServiceProvider, (ITreatmentService)service);
        return await act(service, publisher);
    }

    private Task<T> WithWriteBackAsync<T>(HttpMessageHandler upstream, Func<ITreatmentService, Task<T>> act) =>
        WithWriteBackAsync(upstream, (service, _) => act(service));

    /// <summary>What the connector reads of every copy <paramref name="upstream"/> holds.</summary>
    private static List<Treatment> Pulled(FakeNightscoutTreatments upstream) =>
        upstream.Documents.Select(d => Pull(d.Document)).ToList();

    private static Treatment Pull(JsonObject document)
    {
        var treatment = JsonSerializer.Deserialize<Treatment>(document.ToJsonString(), JsonDefaults.CaseInsensitive)!;
        treatment.Id = (string)document["_id"]!;
        treatment.UpstreamIdentifier = (string?)document["identifier"];
        treatment.DataSource = DataSources.NightscoutConnector;
        return treatment;
    }

    /// <summary>
    /// Pulls <paramref name="pulled"/> as the connector does, through a publisher whose treatment service
    /// writes back to <paramref name="upstream"/>, and asserts it sent nothing there.
    /// </summary>
    private async Task PullAsync(FakeNightscoutTreatments upstream, IEnumerable<Treatment> pulled)
    {
        var writes = upstream.Writes.Count;
        (await WithWriteBackAsync(upstream, (_, publisher) =>
            publisher.PublishTreatmentsAsync(pulled, DataSources.NightscoutConnector, WriteOrigin.Live))).Should().BeTrue();
        upstream.Writes.Should().HaveCount(writes);
    }

    private Task PullAsync(FakeNightscoutTreatments upstream) => PullAsync(upstream, Pulled(upstream));

    private static string NewKey(string shape) => shape switch
    {
        "objectid" => MongoObjectId.NewObjectId(),
        "uuid" => Guid.NewGuid().ToString(),
        _ => $"integration-span-{Guid.NewGuid():N}",
    };

    private static Treatment Upload(string eventType, string key, DateTimeOffset slot, double? duration = 30) => eventType switch
    {
        Override => new Treatment
        {
            Id = key, EventType = Override, CreatedAt = At(slot), Duration = duration, Reason = "Running",
            InsulinNeedsScaleFactor = 0.8, EnteredBy = "integration-loop",
        },
        TemporaryTarget => new Treatment
        {
            Id = key, EventType = TemporaryTarget, CreatedAt = At(slot), Duration = duration,
            TargetTop = 140, TargetBottom = 120, Units = "mg/dl", EnteredBy = "integration-loop",
        },
        PumpSuspend => new Treatment { Id = key, EventType = PumpSuspend, CreatedAt = At(slot), EnteredBy = "integration-loop" },
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    /// <summary>The category a treatment of <paramref name="eventType"/> is stored as.</summary>
    private static string CategoryOf(string eventType) => eventType switch
    {
        Override => nameof(StateSpanCategory.Override),
        TemporaryTarget => nameof(StateSpanCategory.TemporaryTarget),
        _ => nameof(StateSpanCategory.PumpMode),
    };

    /// <summary>Every span of <paramref name="eventType"/>'s category starting at <paramref name="slot"/>, deleted ones included.</summary>
    private async Task<List<StateSpanEntity>> SpansAsync(string eventType, DateTimeOffset slot)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var category = CategoryOf(eventType);
        return await db.StateSpans.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == Fixture.TenantId && s.Category == category && s.StartTimestamp == slot.UtcDateTime)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync();
    }

    private async Task<StateSpanEntity> TheLiveSpanAsync(string eventType, DateTimeOffset slot) =>
        (await SpansAsync(eventType, slot)).Should().ContainSingle(s => s.DeletedAt == null).Subject;

    private async Task<int> LiveDeviceEventsAsync(DateTimeOffset slot)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return await db.DeviceEvents.AsNoTracking().CountAsync(e => e.Timestamp == slot.UtcDateTime);
    }

    /// <summary>Writes <paramref name="eventType"/> back to a fresh upstream of <paramref name="version"/>.</summary>
    private async Task<(FakeNightscoutTreatments Upstream, string Key)> WriteBackAsync(
        string eventType, string shape, DateTimeOffset slot, string version = "15.0.8")
    {
        var upstream = new FakeNightscoutTreatments(version);
        var key = NewKey(shape);
        await WithWriteBackAsync(upstream, s => s.CreateTreatmentsAsync([Upload(eventType, key, slot)]));
        upstream.Documents.Should().ContainSingle();
        return (upstream, key);
    }

    public static TheoryData<string, string> CategoriesAndShapes()
    {
        var data = new TheoryData<string, string>();
        foreach (var eventType in new[] { Override, TemporaryTarget, PumpSuspend })
        foreach (var shape in new[] { "objectid", "uuid", "other" })
            data.Add(eventType, shape);
        return data;
    }

    public static TheoryData<string> Categories => new() { Override, TemporaryTarget, PumpSuspend };

    public static TheoryData<string> SpanCategories => new() { Override, TemporaryTarget };

    /// <summary>
    /// Create, send, pull of the echo: the copy 15.0.8 holds under a <c>_id</c> it minted lands on
    /// the span it came from and stores nothing, whatever the shape of the key it went up under.
    /// </summary>
    [Theory]
    [MemberData(nameof(CategoriesAndShapes))]
    public async Task AWrittenBackSpanPulledBack_LandsOnItsOwnSpanAndStoresNothing(string eventType, string shape)
    {
        var slot = UniqueSlot();
        var (upstream, _) = await WriteBackAsync(eventType, shape, slot);
        upstream.Documents.Single().IsObjectId.Should().BeTrue();
        var before = await TheLiveSpanAsync(eventType, slot);

        await PullAsync(upstream);

        (await SpansAsync(eventType, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
        if (eventType == PumpSuspend)
            (await LiveDeviceEventsAsync(slot)).Should().Be(1);
    }

    /// <summary>
    /// Up to 15.0.6 the copy is held under the key write-back sent, as a string <c>_id</c>, with that
    /// key as its identifier too. It lands on its span as the minted copy does.
    /// </summary>
    [Theory]
    [MemberData(nameof(CategoriesAndShapes))]
    public async Task AWrittenBackSpanPulledBackFrom1506_LandsOnItsOwnSpanAndStoresNothing(string eventType, string shape)
    {
        var slot = UniqueSlot();
        var (upstream, key) = await WriteBackAsync(eventType, shape, slot, version: "15.0.6");
        upstream.Documents.Single().Id.Should().Be(MongoObjectId.Coerce(key));
        var before = await TheLiveSpanAsync(eventType, slot);

        await PullAsync(upstream);

        (await SpansAsync(eventType, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
    }

    public static TheoryData<string, string> SpanCategoriesAndOwnIdForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var eventType in new[] { Override, TemporaryTarget })
        foreach (var form in new[] { "prefix", "uuid" })
            data.Add(eventType, form);
        return data;
    }

    /// <summary>
    /// Earlier write-backs sent a span's treatment under the id a v1 read serves it by, whatever its
    /// key: its own uuid's 24-hex prefix, and up to v0.2.3 the full uuid, with no identifier. Such a
    /// copy, still upstream, lands on its span as its echo.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanCategoriesAndOwnIdForms))]
    public async Task ACopyLeftUpstreamUnderTheSpansOwnId_LandsOnItsOwnSpanAndStoresNothing(string eventType, string form)
    {
        var slot = UniqueSlot();
        var (upstream, _) = await WriteBackAsync(eventType, "other", slot);
        var before = await TheLiveSpanAsync(eventType, slot);
        var copy = Upload(eventType, form == "prefix" ? ServedId(before) : before.Id.ToString(), slot, duration: 60);
        copy.DataSource = DataSources.NightscoutConnector;

        await PullAsync(upstream, [copy]);

        (await SpansAsync(eventType, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// The connector reads the same copy on every sync, and its catch-up pass compares a recent copy
    /// with what is stored: neither stores it again.
    /// </summary>
    [Theory]
    [MemberData(nameof(Categories))]
    public async Task AnEchoPulledOnEverySync_IsNeitherStoredNorRepublished(string eventType)
    {
        var slot = UniqueSlot();
        var (upstream, _) = await WriteBackAsync(eventType, "uuid", slot);
        var before = await TheLiveSpanAsync(eventType, slot);

        await PullAsync(upstream);
        await PullAsync(upstream);
        (await WithWriteBackAsync(upstream, (_, publisher) => publisher.PublishRecentTreatmentsAsync(
            Pulled(upstream), DataSources.NightscoutConnector, WriteOrigin.Live))).Should().Be(0);

        (await SpansAsync(eventType, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
    }

    /// <summary>The id a v1 read serves a span's treatment under, the one a client edits it by.</summary>
    private static string ServedId(StateSpanEntity span) => MongoObjectId.FromGuid(span.Id);

    /// <summary>
    /// Local edit, send of the edit, pull of its echo: the edit lands on the copy upstream holds, and
    /// its echo stores nothing, so the span keeps the edit.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanCategories))]
    public async Task ALocalEditPulledBack_KeepsTheEditOnItsOneSpan(string eventType)
    {
        var slot = UniqueSlot();
        var (upstream, key) = await WriteBackAsync(eventType, "other", slot);
        var span = await TheLiveSpanAsync(eventType, slot);

        await WithWriteBackAsync(upstream, s => s.UpdateTreatmentAsync(ServedId(span), Upload(eventType, key, slot, duration: 45)));
        upstream.Documents.Should().ContainSingle().Which.Document["duration"]!.GetValue<double>().Should().Be(45);

        await PullAsync(upstream);

        var after = (await SpansAsync(eventType, slot)).Should().ContainSingle().Subject;
        after.Id.Should().Be(span.Id);
        after.EndTimestamp.Should().Be(slot.AddMinutes(45).UtcDateTime);
        after.Source.Should().Be(span.Source);
    }

    /// <summary>
    /// A pull that read the copy before a local edit's send landed brings back the old version. It is
    /// still the span's echo, so the edit stands.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanCategories))]
    public async Task AStaleCopyPulledAfterALocalEdit_LeavesTheEdit(string eventType)
    {
        var slot = UniqueSlot();
        var (upstream, key) = await WriteBackAsync(eventType, "objectid", slot);
        var stale = Pulled(upstream);
        var span = await TheLiveSpanAsync(eventType, slot);

        await WithWriteBackAsync(upstream, s => s.UpdateTreatmentAsync(ServedId(span), Upload(eventType, key, slot, duration: 45)));
        await PullAsync(upstream, stale);

        (await SpansAsync(eventType, slot)).Should().ContainSingle()
            .Which.EndTimestamp.Should().Be(slot.AddMinutes(45).UtcDateTime);
    }

    /// <summary>
    /// An edit made upstream to a written-back copy keeps its identifier, so the copy still names a
    /// span a live write touched. With no record of what write-back sent, it cannot be told from an
    /// echo and is dropped as one: the span keeps what Nocturne holds, and nothing is stored twice.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanCategories))]
    public async Task AnUpstreamEditOfAWrittenBackSpan_IsDroppedAsAnEcho(string eventType)
    {
        var slot = UniqueSlot();
        var (upstream, _) = await WriteBackAsync(eventType, "uuid", slot);
        var before = await TheLiveSpanAsync(eventType, slot);
        upstream.Documents.Single().Document["duration"] = 90;

        await PullAsync(upstream);

        (await SpansAsync(eventType, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// A span a Nightscout migration imported was never written back: the upstream document is its
    /// own, and an edit made there lands on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanCategories))]
    public async Task AnUpstreamEditOfAMigratedSpan_LandsOnIt(string eventType)
    {
        var slot = UniqueSlot();
        var key = MongoObjectId.NewObjectId();
        await using (var scope = OpenTenantScope())
            await scope.ServiceProvider.GetRequiredService<ITreatmentService>().ImportTreatmentsAsync([Upload(eventType, key, slot)]);
        var before = await TheLiveSpanAsync(eventType, slot);
        before.WrittenLive.Should().BeFalse();

        var edited = Upload(eventType, key, slot, duration: 90);
        edited.DataSource = DataSources.NightscoutConnector;
        await PullAsync(new FakeNightscoutTreatments("15.0.8"), [edited]);

        var after = (await SpansAsync(eventType, slot)).Should().ContainSingle().Subject;
        after.Id.Should().Be(before.Id);
        after.EndTimestamp.Should().Be(slot.AddMinutes(90).UtcDateTime);
    }

    /// <summary>
    /// Local end: an override with no duration is open until a later one supersedes it. The echo of
    /// the open override, pulled after, leaves it ended where the later one began.
    /// </summary>
    [Fact]
    public async Task AnOverrideEndedLocally_StaysEndedWhenItsOpenEchoIsPulled()
    {
        var slot = UniqueSlot();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await WithWriteBackAsync(upstream, s => s.CreateTreatmentsAsync([Upload(Override, NewKey("uuid"), slot, duration: null)]));
        (await TheLiveSpanAsync(Override, slot)).EndTimestamp.Should().BeNull();
        await WithWriteBackAsync(upstream, s => s.CreateTreatmentsAsync([Upload(Override, NewKey("uuid"), slot.AddMinutes(10))]));
        var ended = await TheLiveSpanAsync(Override, slot);
        ended.EndTimestamp.Should().Be(slot.AddMinutes(10).UtcDateTime);
        var later = await TheLiveSpanAsync(Override, slot.AddMinutes(10));

        await PullAsync(upstream);

        (await SpansAsync(Override, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(ended);
        (await SpansAsync(Override, slot.AddMinutes(10))).Should().ContainSingle().Which.Should().BeEquivalentTo(later);
    }

    /// <summary>
    /// Upstream end: a temporary target cancelled in Nightscout arrives as a document of its own, with
    /// its own <c>_id</c> and no identifier. It is stored as the connector's span; the written-back
    /// target is not stored again.
    /// </summary>
    [Fact]
    public async Task ATargetCancelledUpstream_IsStoredOnceBesideTheWrittenBackTarget()
    {
        var slot = UniqueSlot();
        var (upstream, _) = await WriteBackAsync(TemporaryTarget, "objectid", slot);
        var before = await TheLiveSpanAsync(TemporaryTarget, slot);
        upstream.Seed(new JsonObject
        {
            ["_id"] = MongoObjectId.NewObjectId(),
            ["eventType"] = TemporaryTarget,
            ["duration"] = 0,
            ["created_at"] = At(slot.AddMinutes(10)),
        }, asObjectId: true);

        await PullAsync(upstream);

        (await SpansAsync(TemporaryTarget, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
        (await SpansAsync(TemporaryTarget, slot.AddMinutes(10))).Should().ContainSingle()
            .Which.Source.Should().Be(DataSources.NightscoutConnector);
    }

    /// <summary>
    /// Local delete, by the user through v1: write-back leaves the copy upstream, and the copy pulled
    /// back finds the user's deletion. Nothing is stored again.
    /// </summary>
    [Theory]
    [MemberData(nameof(Categories))]
    public async Task ASpanDeletedLocally_StaysDeletedWhenItsCopyIsPulled(string eventType)
    {
        var slot = UniqueSlot();
        var (upstream, _) = await WriteBackAsync(eventType, "other", slot);
        var span = await TheLiveSpanAsync(eventType, slot);
        var servedId = eventType == PumpSuspend ? await ServedDeviceEventIdAsync(slot) : ServedId(span);
        (await AuthenticatedClient.DeleteAsync($"/api/v1/treatments/{servedId}")).IsSuccessStatusCode.Should().BeTrue();
        if (eventType != PumpSuspend)
            (await SpansAsync(eventType, slot)).Should().OnlyContain(s => s.DeletedAt != null);
        var deleted = await SpansAsync(eventType, slot);

        await PullAsync(upstream);

        (await SpansAsync(eventType, slot)).Should().BeEquivalentTo(deleted);
        if (eventType == PumpSuspend)
            (await LiveDeviceEventsAsync(slot)).Should().Be(0);
    }

    private async Task<string> ServedDeviceEventIdAsync(DateTimeOffset slot)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return MongoObjectId.FromGuid(await db.DeviceEvents.AsNoTracking()
            .Where(e => e.Timestamp == slot.UtcDateTime).Select(e => e.Id).SingleAsync());
    }

    /// <summary>
    /// Upstream delete: the connector removes, of what a pull no longer finds, only the records it
    /// stored itself. A written-back span is not one of them, so it stays.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanCategories))]
    public async Task AWrittenBackSpanDeletedUpstream_Stays(string eventType)
    {
        var slot = UniqueSlot();
        var (upstream, key) = await WriteBackAsync(eventType, "objectid", slot);
        var before = await TheLiveSpanAsync(eventType, slot);

        await using (var scope = OpenTenantScope())
            await scope.ServiceProvider.GetRequiredService<ITreatmentService>().DeleteFromSourceAsync(
                DataSources.NightscoutConnector, new HashSet<string> { key, upstream.Documents.Single().Id });

        (await SpansAsync(eventType, slot)).Should().ContainSingle().Which.Should().BeEquivalentTo(before);
    }
}
