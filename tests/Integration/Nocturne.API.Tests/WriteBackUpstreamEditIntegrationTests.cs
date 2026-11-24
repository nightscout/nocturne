using System.Net.Http.Json;
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
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment written back to Nightscout, then pulled back by the connector, against Postgres. The
/// pulled copy of what write-back sent is its echo: it stores nothing, raises nothing and sends
/// nothing. A copy edited upstream, such as AAPS changing a dose, reaches Nocturne under the record's
/// own data source and is not sent back, and the pull after it is an echo again. A copy marked invalid
/// upstream deletes the record.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class WriteBackUpstreamEditIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string UpstreamUrl = "https://nightscout.example.com";
    private const string Uploader = "aaps-uploader";

    private static DateTimeOffset UniqueSlot() =>
        new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 150_000) * 20);

    private static string At(DateTimeOffset slot) => slot.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private AsyncServiceScope OpenTenantScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:write-back-upstream-edit");

    private static NightscoutTreatmentWriteBackSink WriteBack(HttpMessageHandler upstream, IServiceProvider services)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NightscoutConnectorConfiguration
        {
            Url = UpstreamUrl,
            ApiSecret = "integration-secret-0001",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50,
        });
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream, disposeHandler: false),
            loader.Object,
            new NightscoutCircuitBreaker(),
            services.GetRequiredService<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private async Task CreateAsync(HttpMessageHandler upstream, Treatment treatment)
    {
        await using var scope = OpenTenantScope();
        var service = ActivatorUtilities.CreateInstance<TreatmentService>(
            scope.ServiceProvider, (IDataEventSink<Treatment>)WriteBack(upstream, scope.ServiceProvider));
        await service.CreateTreatmentsAsync([treatment]);
    }

    private async Task UpdateAsync(HttpMessageHandler upstream, string legacyId, Treatment edit)
    {
        await using var scope = OpenTenantScope();
        var service = ActivatorUtilities.CreateInstance<TreatmentService>(
            scope.ServiceProvider, (IDataEventSink<Treatment>)WriteBack(upstream, scope.ServiceProvider));
        (await service.UpdateTreatmentAsync(legacyId, edit)).Should().NotBeNull();
    }

    /// <summary>The events a pull raised, besides the write-back each one is handed to.</summary>
    private sealed class Raised(IDataEventSink<Treatment> writeBack) : IDataEventSink<Treatment>
    {
        public List<Treatment> Created { get; } = [];
        public List<Treatment> Updated { get; } = [];

        public Task OnCreatedAsync(IReadOnlyList<Treatment> items, CancellationToken ct = default)
        {
            Created.AddRange(items);
            return writeBack.OnCreatedAsync(items, ct);
        }

        public Task OnUpdatedAsync(Treatment item, CancellationToken ct = default)
        {
            Updated.Add(item);
            return writeBack.OnUpdatedAsync(item, ct);
        }
    }

    /// <summary>Publishes <paramref name="served"/> as the Nightscout connector's pull does, writing back to <paramref name="upstream"/>.</summary>
    private async Task<Raised> PullAsync(HttpMessageHandler upstream, IEnumerable<JsonObject> served)
    {
        var pulled = served.Select(document =>
        {
            var treatment = JsonSerializer.Deserialize<Treatment>(document.ToJsonString(), UpstreamIdentityJson.ReadOptions)!;
            treatment.UpstreamIdentifier = (string?)document["identifier"];
            treatment.DataSource = DataSources.NightscoutConnector;
            return treatment;
        }).ToList();

        await using var scope = OpenTenantScope();
        var raised = new Raised(WriteBack(upstream, scope.ServiceProvider));
        var service = ActivatorUtilities.CreateInstance<TreatmentService>(scope.ServiceProvider, (IDataEventSink<Treatment>)raised);
        var publisher = ActivatorUtilities.CreateInstance<TreatmentPublisher>(scope.ServiceProvider, (ITreatmentService)service);
        (await publisher.PublishTreatmentsAsync(pulled, DataSources.NightscoutConnector, WriteOrigin.Live)).Should().BeTrue();
        return raised;
    }

    /// <param name="SentFingerprint">The sends upstream may hold (<see cref="WriteBackFingerprint.Sends"/>), without the earlier ones.</param>
    private sealed record StoredRow(double Amount, string? DataSource, bool WrittenLive, string? SentFingerprint);

    private static StoredRow Row(double amount, string? dataSource, bool writtenLive, string? stored) =>
        new(amount, dataSource, writtenLive,
            stored is null ? null : string.Join(';', WriteBackFingerprint.Sends(stored).Select(WriteBackFingerprint.Stored)));

    private async Task<List<StoredRow>> BolusesAsync(string legacyId)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return [.. (await db.Boluses.AsNoTracking().Where(b => b.LegacyId == legacyId).ToListAsync())
            .Select(b => Row(b.Insulin, b.DataSource, b.WrittenLive, b.WriteBackSentFingerprint))];
    }

    private async Task<List<StoredRow>> CarbIntakesAsync(string legacyId)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return [.. (await db.CarbIntakes.AsNoTracking().Where(c => c.LegacyId == legacyId).ToListAsync())
            .Select(c => Row(c.Carbs, c.DataSource, c.WrittenLive, c.WriteBackSentFingerprint))];
    }

    private static string FingerprintOf(JsonObject document) =>
        WriteBackFingerprint.Of(JsonSerializer.Deserialize<Treatment>(document.ToJsonString(), UpstreamIdentityJson.ReadOptions)!);

    private static Treatment Meal(string legacyId, DateTimeOffset slot, double? preBolus = null) => new()
    {
        Id = legacyId, EventType = "Meal Bolus", Insulin = 2, Carbs = 30, PreBolus = preBolus, CreatedAt = At(slot), DataSource = Uploader,
    };

    private static List<JsonObject> Held(FakeNightscoutTreatments upstream) => [.. upstream.Documents.Select(d => d.Document)];

    /// <summary>What <paramref name="upstream"/> holds now, as a pull reads it: later writes upstream leave it as it is.</summary>
    private static List<JsonObject> Copied(FakeNightscoutTreatments upstream) => [.. Held(upstream).Select(d => d.DeepClone().AsObject())];

    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnUnchangedCopy_StoresNothing_RaisesNothing_AndSendsNothing(string version)
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments(version);
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        var before = await BolusesAsync(legacyId);
        var writes = upstream.Writes.Count;

        var raised = await PullAsync(upstream, Held(upstream));

        before.Should().ContainSingle().Which.SentFingerprint.Should().Be(FingerprintOf(Held(upstream).Single()));
        (await BolusesAsync(legacyId)).Should().Equal(before);
        raised.Created.Should().BeEmpty();
        raised.Updated.Should().BeEmpty();
        upstream.Writes.Count.Should().Be(writes);
    }

    public static TheoryData<string, string> Edits => new()
    {
        { "15.0.8", "insulin" },
        { "15.0.6", "insulin" },
        { "15.0.8", "carbs" },
        { "15.0.6", "carbs" },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task AnUpstreamEdit_IsAppliedUnderTheRecordsOwnSource_IsNotSentBack_AndTheNextPullIsAnEcho(string version, string field)
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments(version);
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        var copy = Held(upstream).Single();
        copy[field] = field == "insulin" ? 3.5 : 55;
        var writes = upstream.Writes.Count;

        var raised = await PullAsync(upstream, [copy]);

        var bolus = (await BolusesAsync(legacyId)).Should().ContainSingle().Subject;
        var carbs = (await CarbIntakesAsync(legacyId)).Should().ContainSingle().Subject;
        bolus.Amount.Should().Be(field == "insulin" ? 3.5 : 2);
        carbs.Amount.Should().Be(field == "carbs" ? 55 : 30);
        new[] { bolus, carbs }.Should().AllSatisfy(row =>
        {
            row.DataSource.Should().Be(Uploader);
            row.WrittenLive.Should().BeTrue();
            row.SentFingerprint.Should().Be(FingerprintOf(copy));
        });
        raised.Updated.Should().ContainSingle();
        upstream.Writes.Count.Should().Be(writes);

        var again = await PullAsync(upstream, [copy]);

        again.Created.Should().BeEmpty();
        again.Updated.Should().BeEmpty();
        upstream.Writes.Count.Should().Be(writes);
    }

    /// <summary>
    /// <c>isValid: false</c> ends as it does on a record write-back never sent: the upload names the
    /// record by its id, so the record is deleted. Write-back sends no delete, and the next pull stores
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnUpstreamInvalidation_DeletesTheRecord_AndIsNotSentBack(string version)
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments(version);
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        var copy = Held(upstream).Single();
        copy["isValid"] = false;
        var writes = upstream.Writes.Count;

        var raised = await PullAsync(upstream, [copy]);

        (await BolusesAsync(legacyId)).Should().BeEmpty();
        (await CarbIntakesAsync(legacyId)).Should().BeEmpty();
        raised.Created.Should().BeEmpty();
        raised.Updated.Should().BeEmpty();
        upstream.Writes.Count.Should().Be(writes);

        var again = await PullAsync(upstream, [copy]);

        again.Created.Should().BeEmpty();
        again.Updated.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().BeEmpty();
        upstream.Writes.Count.Should().Be(writes);
    }

    /// <summary>
    /// A meal stored as one record goes upstream as its bolus and its carbs. Each document pulled back
    /// unchanged is an echo. One changed upstream names the whole meal's record, which it cannot be
    /// written to alone, so the meal is kept as it is.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "insulin")]
    [InlineData(false, "carbs")]
    [InlineData(true, "carbs")]
    public async Task ASplitMealsDocuments_EachLeaveTheMealAsItIs(bool v15_0_8, string? changed)
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        await CreateAsync(upstream, Meal(legacyId, slot, preBolus: 15));
        var served = JsonNode.Parse(upstream.Served())!.AsArray().Select(d => d!.AsObject()).ToList();
        served.Should().HaveCount(2);
        if (changed is not null)
            served.Single(d => d[changed] is not null)[changed] = 99;
        var bolusBefore = await BolusesAsync(legacyId);
        var carbsBefore = await CarbIntakesAsync(legacyId);

        var raised = await PullAsync(upstream, served);

        var sent = WriteBackFingerprint.Stored(
            [.. JsonNode.Parse(upstream.Served())!.AsArray().OrderBy(d => d!["carbs"] is null ? 0 : 1).Select(d => FingerprintOf(d!.AsObject()))]);
        bolusBefore.Should().ContainSingle().Which.SentFingerprint.Should().Be(sent);
        carbsBefore.Should().ContainSingle().Which.SentFingerprint.Should().Be(sent);
        (await BolusesAsync(legacyId)).Should().Equal(bolusBefore);
        (await CarbIntakesAsync(legacyId)).Should().Equal(carbsBefore);
        raised.Created.Should().BeEmpty();
        raised.Updated.Should().BeEmpty();
    }

    /// <summary>
    /// An upstream edit applied to a record keeps it the record's own, so an edit made in Nocturne
    /// afterwards is still written back, and the pull after that is an echo.
    /// </summary>
    [Fact]
    public async Task ANocturneEditAfterAnAppliedUpstreamEdit_IsWrittenBack()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, slot));
        var copy = Held(upstream).Single();
        copy["insulin"] = 3.5;
        await PullAsync(upstream, [copy]);
        var writes = upstream.Writes.Count;

        var edit = Meal(legacyId, slot);
        edit.Insulin = 5;
        await UpdateAsync(upstream, legacyId, edit);

        upstream.Writes.Count.Should().BeGreaterThan(writes);
        var held = Held(upstream).Single();
        ((double)held["insulin"]!).Should().Be(5);
        (await BolusesAsync(legacyId)).Should().ContainSingle()
            .Which.Should().Be(new StoredRow(5, Uploader, true, FingerprintOf(held)));
        var again = await PullAsync(upstream, [held]);
        again.Updated.Should().BeEmpty();
    }

    /// <summary>
    /// An upstream edit that turns a meal into a correction drops its carbs: the record keeps no live
    /// carb intake, what is left matches the edited document, and the next pull is an echo.
    /// </summary>
    [Fact]
    public async Task AnUpstreamEditThatDropsTheCarbs_LeavesOnlyTheBolus()
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        var copy = Held(upstream).Single();
        copy["eventType"] = "Correction Bolus";
        copy.Remove("carbs");
        var writes = upstream.Writes.Count;

        var raised = await PullAsync(upstream, [copy]);

        (await BolusesAsync(legacyId)).Should().ContainSingle()
            .Which.Should().Be(new StoredRow(2, Uploader, true, FingerprintOf(copy)));
        (await CarbIntakesAsync(legacyId)).Should().BeEmpty();
        raised.Updated.Should().HaveCount(1);
        upstream.Writes.Count.Should().Be(writes);

        var again = await PullAsync(upstream, [copy]);

        again.Created.Should().BeEmpty();
        again.Updated.Should().BeEmpty();
    }

    /// <summary>
    /// The user deleted a meal's carb intake in Nocturne, so its tombstone keeps what write-back sent
    /// before. Each later upstream edit still reaches the live bolus.
    /// </summary>
    [Fact]
    public async Task UpstreamEdits_ReachARecordWhoseCarbIntakeTheUserDeleted()
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        await using (var db = Fixture.CreateDbContext(Fixture.TenantId))
        {
            await db.CarbIntakes.Where(c => c.LegacyId == legacyId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.DeletedAt, DateTime.UtcNow)
                .SetProperty(c => EF.Property<bool>(c, NocturneDbContext.DeletedByUserProperty), true));
        }

        foreach (var insulin in new[] { 3.5, 4.5 })
        {
            var copy = Held(upstream).Single();
            copy["insulin"] = insulin;

            var raised = await PullAsync(upstream, [copy]);

            raised.Updated.Should().HaveCount(1);
            (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(insulin);
        }
    }

    /// <summary>
    /// A record written back before fingerprints were kept has none: any copy of it pulled back is
    /// still taken as its echo, until write-back next sends it.
    /// </summary>
    [Fact]
    public async Task ARecordWrittenBackBeforeFingerprintsWereKept_TakesAnyCopyAsItsEcho()
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        await using (var db = Fixture.CreateDbContext(Fixture.TenantId))
        {
            await db.Boluses.Where(b => b.LegacyId == legacyId).ExecuteUpdateAsync(u => u.SetProperty(b => b.WriteBackSentFingerprint, (string?)null));
            await db.CarbIntakes.Where(c => c.LegacyId == legacyId).ExecuteUpdateAsync(u => u.SetProperty(c => c.WriteBackSentFingerprint, (string?)null));
        }
        var copy = Held(upstream).Single();
        copy["insulin"] = 3.5;

        var raised = await PullAsync(upstream, [copy]);

        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Should().Be(new StoredRow(2, Uploader, true, null));
        raised.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task WasSentUpstream_IsAnsweredForARecordWriteBackSent()
    {
        var sent = MongoObjectId.NewObjectId();
        var local = MongoObjectId.NewObjectId();
        await CreateAsync(new FakeNightscoutTreatments("15.0.8"), Meal(sent, UniqueSlot()));
        await CreateAsync(new Refusing(), Meal(local, UniqueSlot()));

        await using var scope = OpenTenantScope();
        (await scope.ServiceProvider.GetRequiredService<IWriteBackSentRecords>().GetSentUpstreamAsync([sent, local]))
            .Should().BeEquivalentTo([sent]);
    }

    /// <summary>
    /// A meal sent whole is edited to split. Upstream takes its bolus, which holds no carbs, and refuses
    /// its carbs. The bolus pulled back is a document of the send Nocturne began, so the meal keeps its
    /// carbs.
    /// </summary>
    [Fact]
    public async Task AMealEditedIntoASplitWhoseCarbsUpstreamRefuses_KeepsItsCarbs()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var held = new FakeNightscoutTreatments("15.0.8");
        var upstream = new Interposed(held);
        await CreateAsync(upstream, Meal(legacyId, slot));
        upstream.RefuseCarbs = true;

        await UpdateAsync(upstream, legacyId, Meal(legacyId, slot, preBolus: 15));
        var bolusOnly = Held(held).Should().ContainSingle().Subject;
        bolusOnly["carbs"].Should().BeNull();

        var raised = await PullAsync(upstream, [bolusOnly]);

        raised.Updated.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(2);
        (await CarbIntakesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(30);
    }

    /// <summary>
    /// A pull that runs once upstream has taken a send and before Nocturne stamps it, here before the
    /// carbs of a meal edited to split are even sent, reads what upstream holds as an echo. The edit
    /// made in Nocturne stays, and so do the meal's carbs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(15.0)]
    public async Task APullBetweenAnAcceptedSendAndItsStamp_AppliesNothing(double? preBolus)
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var held = new FakeNightscoutTreatments("15.0.8");
        var upstream = new Interposed(held);
        await CreateAsync(upstream, Meal(legacyId, slot));
        Raised? raisedBetween = null;
        upstream.OnAccepted = async () => raisedBetween = await PullAsync(upstream, Held(held));
        var edit = Meal(legacyId, slot, preBolus);
        edit.Insulin = 5;

        await UpdateAsync(upstream, legacyId, edit);

        raisedBetween.Should().NotBeNull();
        raisedBetween!.Created.Should().BeEmpty();
        raisedBetween.Updated.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(5);
        (await CarbIntakesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(30);
        var again = await PullAsync(upstream, Held(held));
        again.Updated.Should().BeEmpty();
    }

    /// <summary>
    /// A send upstream refuses whole leaves a record never sent reading as never sent, and one sent
    /// before as sent. The copy upstream still holds of the latter is still its echo.
    /// </summary>
    [Fact]
    public async Task ASendUpstreamRefusesWhole_LeavesWhetherTheRecordWasSentAsItWas()
    {
        var neverSent = MongoObjectId.NewObjectId();
        var sentBefore = MongoObjectId.NewObjectId();
        var held = new FakeNightscoutTreatments("15.0.8");
        var neverSlot = UniqueSlot();
        var sentSlot = UniqueSlot();
        await CreateAsync(new Refusing(), Meal(neverSent, neverSlot));
        await CreateAsync(held, Meal(sentBefore, sentSlot));

        await UpdateAsync(new Refusing(), neverSent, Meal(neverSent, neverSlot, preBolus: 15));
        var sentEdit = Meal(sentBefore, sentSlot, preBolus: 15);
        sentEdit.Insulin = 5;
        await UpdateAsync(new Refusing(), sentBefore, sentEdit);

        await using (var scope = OpenTenantScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IWriteBackSentRecords>().GetSentUpstreamAsync([neverSent, sentBefore]))
                .Should().BeEquivalentTo([sentBefore]);
        }

        (await BolusesAsync(neverSent)).Should().ContainSingle().Which.SentFingerprint.Should().BeNull();
        (await CarbIntakesAsync(neverSent)).Should().ContainSingle().Which.SentFingerprint.Should().BeNull();
        var raised = await PullAsync(held, Held(held));
        raised.Updated.Should().BeEmpty();
        (await BolusesAsync(sentBefore)).Should().ContainSingle().Which.Amount.Should().Be(5);
        (await CarbIntakesAsync(sentBefore)).Should().ContainSingle().Which.Amount.Should().Be(30);
    }

    /// <summary>
    /// A document under the id write-back sends a meal's split-off carbs under can only be such carbs,
    /// left upstream by an earlier send. Pulled back changed while the meal went upstream whole, it is
    /// not applied to the meal, which it would strip of its bolus.
    /// </summary>
    [Fact]
    public async Task ACopyUnderTheSplitOffCarbsId_IsNotAppliedToAMealSentWhole()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, slot));
        var carbsId = PreBolusSplit.WriteBackCarbsId(legacyId);
        var staleCarbs = new JsonObject
        {
            ["_id"] = MongoObjectId.Coerce(carbsId),
            ["identifier"] = carbsId,
            ["eventType"] = "Meal Bolus",
            ["carbs"] = 45,
            ["created_at"] = At(slot.AddMinutes(15)),
        };

        var raised = await PullAsync(upstream, [staleCarbs]);

        raised.Created.Should().BeEmpty();
        raised.Updated.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(2);
        (await CarbIntakesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(30);
    }

    /// <summary>
    /// A record created through v4 has no legacy id, so write-back sends it under its own uuid and
    /// stamps it by that uuid. An upstream edit of it takes the id it came back under as its legacy id,
    /// is applied and stamped under it, and the next pull is an echo.
    /// </summary>
    [Fact]
    public async Task AnUpstreamEditOfARecordWithNoLegacyId_IsAppliedOnce()
    {
        var slot = UniqueSlot();
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/insulin/boluses", new { timestamp = slot, insulin = 2.5 });
        created.IsSuccessStatusCode.Should().BeTrue();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await using (var scope = OpenTenantScope())
        {
            var treatment = await scope.ServiceProvider.GetRequiredService<ITreatmentService>().GetTreatmentByIdAsync(id.ToString());
            treatment.Should().NotBeNull();
            treatment!.LegacyId.Should().BeNull();
            await WriteBack(upstream, scope.ServiceProvider).OnCreatedAsync([treatment]);
        }

        var copy = Held(upstream).Should().ContainSingle().Subject;
        (await BolusRowAsync(id)).SentFingerprint.Should().Be(FingerprintOf(copy));
        copy["insulin"] = 4;

        var raised = await PullAsync(upstream, [copy]);

        raised.Updated.Should().HaveCount(1);
        var row = await BolusRowAsync(id);
        row.Amount.Should().Be(4);
        row.SentFingerprint.Should().Be(FingerprintOf(copy));
        var again = await PullAsync(upstream, [copy]);
        again.Created.Should().BeEmpty();
        again.Updated.Should().BeEmpty();
    }

    public static TheoryData<string, string> StaleReads => new()
    {
        { "before the send", "whole" },
        { "before the send", "split to whole" },
        { "before the send", "whole to split" },
        { "between the send and its stamp", "whole" },
        { "between the send and its stamp", "split to whole" },
        { "between the send and its stamp", "whole to split" },
        { "after the stamp", "whole" },
        { "after the stamp", "split to whole" },
        { "after the stamp", "whole to split" },
    };

    /// <summary>
    /// A pull reads what upstream holds, then the user edits the meal in Nocturne and write-back sends
    /// it, and the pull plans what it read at some point of that send, as a reconcile pass that runs
    /// after its crawl does. What it read is a send Nocturne made, so it applies nothing: the edit
    /// stays, and so do the meal's carbs.
    /// </summary>
    [Theory]
    [MemberData(nameof(StaleReads))]
    public async Task APullReadBeforeAnEdit_AppliesNothing_WhenEverItIsPlanned(string planned, string shape)
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var held = new FakeNightscoutTreatments("15.0.8");
        var upstream = new Interposed(held);
        await CreateAsync(upstream, Meal(legacyId, slot, shape == "split to whole" ? 15 : null));
        var read = Copied(held);
        read.Should().HaveCount(shape == "split to whole" ? 2 : 1);
        Raised? stale = null;
        if (planned == "before the send")
            upstream.BeforeWrite = async () => stale = await PullAsync(upstream, read);
        else if (planned == "between the send and its stamp")
            upstream.OnAccepted = async () => stale = await PullAsync(upstream, read);
        var edit = Meal(legacyId, slot, shape == "whole to split" ? 15 : null);
        edit.Insulin = 5;

        await UpdateAsync(upstream, legacyId, edit);
        if (planned == "after the stamp")
            stale = await PullAsync(upstream, read);

        stale.Should().NotBeNull();
        stale!.Created.Should().BeEmpty();
        stale.Updated.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(5);
        (await CarbIntakesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(30);
        var again = await PullAsync(upstream, Held(held));
        again.Updated.Should().BeEmpty();
    }

    /// <summary>
    /// A pull reads what upstream holds, another applies an edit made upstream since, and then the first
    /// plans what it read. That is the send the applied edit replaced, so it reverts nothing.
    /// </summary>
    [Fact]
    public async Task APullReadBeforeAnAppliedUpstreamEdit_DoesNotRevertIt()
    {
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));
        var read = Copied(upstream);
        var edited = Held(upstream).Single();
        edited["insulin"] = 3.5;
        (await PullAsync(upstream, [edited])).Updated.Should().HaveCount(1);

        var stale = await PullAsync(upstream, read);

        stale.Updated.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(3.5);
    }

    /// <summary>
    /// A pull that runs once upstream has taken the first send of a record and before Nocturne stamps
    /// it reads the copy as an echo, and the record reads as sent only once it is stamped.
    /// </summary>
    [Fact]
    public async Task APullBetweenAFirstSendAndItsStamp_AppliesNothing()
    {
        var legacyId = MongoObjectId.NewObjectId();
        var held = new FakeNightscoutTreatments("15.0.8");
        var upstream = new Interposed(held);
        Raised? between = null;
        IReadOnlySet<string>? sentBetween = null;
        upstream.OnAccepted = async () =>
        {
            between = await PullAsync(upstream, Held(held));
            await using var scope = OpenTenantScope();
            sentBetween = await scope.ServiceProvider.GetRequiredService<IWriteBackSentRecords>().GetSentUpstreamAsync([legacyId]);
        };

        await CreateAsync(upstream, Meal(legacyId, UniqueSlot()));

        between.Should().NotBeNull();
        between!.Created.Should().BeEmpty();
        between.Updated.Should().BeEmpty();
        sentBetween.Should().BeEmpty();
        (await BolusesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(2);
        await using var after = OpenTenantScope();
        (await after.ServiceProvider.GetRequiredService<IWriteBackSentRecords>().GetSentUpstreamAsync([legacyId]))
            .Should().BeEquivalentTo([legacyId]);
    }

    /// <summary>
    /// An edit into a split meal that upstream refuses whole leaves that send begun. Sending the meal
    /// again as it was before replaces it, so a later upstream edit of the whole meal is applied
    /// rather than read as a changed half of the split.
    /// </summary>
    [Fact]
    public async Task AMealSentAgainAsItWasAfterARefusedSplit_TakesUpstreamEditsAgain()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await CreateAsync(upstream, Meal(legacyId, slot));
        var first = FingerprintOf(Held(upstream).Single());
        await UpdateAsync(new Refusing(), legacyId, Meal(legacyId, slot, preBolus: 15));
        await UpdateAsync(upstream, legacyId, Meal(legacyId, slot));
        var copy = Held(upstream).Should().ContainSingle().Subject;
        FingerprintOf(copy).Should().Be(first);
        copy["carbs"] = 55;

        var raised = await PullAsync(upstream, [copy]);

        raised.Updated.Should().HaveCount(1);
        (await CarbIntakesAsync(legacyId)).Should().ContainSingle().Which.Amount.Should().Be(55);
    }

    private async Task<StoredRow> BolusRowAsync(Guid id)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var row = await db.Boluses.AsNoTracking().SingleAsync(b => b.Id == id);
        return Row(row.Insulin, row.DataSource, row.WrittenLive, row.WriteBackSentFingerprint);
    }

    /// <summary>
    /// An upstream that refuses a write holding carbs once <see cref="RefuseCarbs"/> is set, runs
    /// <see cref="BeforeWrite"/> once, before the next write reaches it, and runs <see cref="OnAccepted"/>
    /// once, after the next write it takes, before answering it.
    /// </summary>
    private sealed class Interposed(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool RefuseCarbs { get; set; }

        public Func<Task>? OnAccepted { get; set; }

        public Func<Task>? BeforeWrite { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && BeforeWrite is { } before)
            {
                BeforeWrite = null;
                await before();
            }

            if (RefuseCarbs && request.Content is not null
                && JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) is { } body
                && (body is JsonArray array ? array.Select(d => d!) : [body]).Any(d => d["carbs"] is not null))
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("refused") };

            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method != HttpMethod.Get && response.IsSuccessStatusCode && OnAccepted is { } accepted)
            {
                OnAccepted = null;
                await accepted();
            }

            return response;
        }
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("refused") });
    }
}
