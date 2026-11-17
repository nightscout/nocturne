using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment deleted through <see cref="TreatmentService"/> against Postgres removes the copy
/// write-back left upstream only when upstream accepted a send of it, so a record written while
/// write-back was off, whose every send failed, or that was only imported keeps whatever upstream
/// holds under its id. Every treatment here is years old: Nightscout's treatment finds default to the
/// last four days.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class TreatmentWriteBackDeleteIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string UpstreamUrl = "https://nightscout.example.com";
    private const string AnotherClient = "another-client";

    /// <summary>A slot no other test writes a bolus into, since the fixture's tenant is shared.</summary>
    private static DateTimeOffset UniqueSlot() =>
        new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 150_000) * 20 + 7);

    private static string At(DateTimeOffset slot) => slot.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private AsyncServiceScope OpenTenantScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:treatment-write-back-delete");

    private async Task<T> WithWriteBackAsync<T>(
        HttpMessageHandler upstream, Func<ITreatmentService, Task<T>> act, bool writeBackEnabled = true)
    {
        await using var scope = OpenTenantScope();

        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NightscoutConnectorConfiguration
        {
            Url = UpstreamUrl,
            ApiSecret = "integration-secret-0001",
            WriteBackEnabled = writeBackEnabled,
            WriteBackBatchSize = 50,
        });
        var sink = new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream, disposeHandler: false),
            loader.Object,
            new NightscoutCircuitBreaker(),
            scope.ServiceProvider.GetRequiredService<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        return await act(ActivatorUtilities.CreateInstance<TreatmentService>(
            scope.ServiceProvider, (IDataEventSink<Treatment>)sink, (ITreatmentUpstreamDeletion)sink));
    }

    private Task CreateAsync(HttpMessageHandler upstream, Treatment treatment, bool writeBackEnabled = true) =>
        WithWriteBackAsync(upstream, async s =>
        {
            await s.CreateTreatmentsAsync([treatment]);
            return true;
        }, writeBackEnabled);

    private static Treatment Bolus(string legacyId, DateTimeOffset slot) => new()
    {
        Id = legacyId, EventType = "Correction Bolus", Insulin = 0.7, CreatedAt = At(slot), DataSource = "careportal",
    };

    /// <summary>An upstream holding another client's dose, which no delete here may remove.</summary>
    private static FakeNightscoutTreatments Upstream(DateTimeOffset slot)
    {
        var upstream = new FakeNightscoutTreatments("15.0.8");
        upstream.Seed(new JsonObject
        {
            ["_id"] = MongoObjectId.NewObjectId(), ["identifier"] = AnotherClient, ["eventType"] = "Correction Bolus", ["insulin"] = 0.3,
            ["created_at"] = At(slot.AddMinutes(5)),
        }, asObjectId: true);
        return upstream;
    }

    /// <summary>Stores a copy under <paramref name="identifier"/>, as a client other than write-back could.</summary>
    private static void Hold(FakeNightscoutTreatments upstream, string identifier, DateTimeOffset at) =>
        upstream.Seed(new JsonObject
        {
            ["_id"] = MongoObjectId.NewObjectId(), ["identifier"] = identifier, ["eventType"] = "Correction Bolus", ["insulin"] = 0.7,
            ["created_at"] = At(at),
        }, asObjectId: true);

    /// <summary>Stamps each of <paramref name="legacyIds"/> as write-back stamps a record once upstream accepts it.</summary>
    private async Task UpstreamAcceptedAsync(params string[] legacyIds)
    {
        await using var scope = OpenTenantScope();
        await scope.ServiceProvider.GetRequiredService<IWriteBackSentRecords>().RecordSentAsync(
            [.. legacyIds.Select(id => new WriteBackSentRecord(id, null, [WriteBackFingerprint.Of(new Treatment { Id = id })]))]);
    }

    /// <summary>
    /// A meal whose bolus and carbs hold different legacy ids, each stored by a live write, as a
    /// connector or an uploader that sends the halves apart leaves it.
    /// </summary>
    /// <returns>The bolus's id, which names the meal.</returns>
    private async Task<Guid> StoreMealAsync(
        DateTimeOffset slot, string bolusLegacyId, string carbsLegacyId, string carbsSource = "careportal")
    {
        await using var scope = OpenTenantScope();
        var correlation = Guid.CreateVersion7();
        var bolus = await scope.ServiceProvider.GetRequiredService<IBolusRepository>().CreateAsync(new Bolus
        {
            Timestamp = slot.UtcDateTime, Insulin = 2, LegacyId = bolusLegacyId, CorrelationId = correlation, DataSource = "careportal",
        }, WriteOrigin.Live);
        await scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>().CreateAsync(new CarbIntake
        {
            Timestamp = slot.UtcDateTime, Carbs = 30, LegacyId = carbsLegacyId, CorrelationId = correlation, DataSource = carbsSource,
        }, WriteOrigin.Live);
        return bolus.Id;
    }

    private static IEnumerable<string?> Identifiers(FakeNightscoutTreatments upstream) =>
        upstream.Documents.Select(d => (string?)d.Document["identifier"]);

    private static int Deletes(FakeNightscoutTreatments upstream) => upstream.Writes.Count(m => m == HttpMethod.Delete);

    private Task<bool> DeleteAsync(FakeNightscoutTreatments upstream, string id) =>
        WithWriteBackAsync(upstream, s => s.DeleteTreatmentAsync(id));

    [Fact]
    public async Task ATreatmentUpstreamAccepted_DeletedByItsId_LosesItsCopyUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        await CreateAsync(upstream, Bolus(legacyId, slot));
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Identifiers(upstream).Should().Equal(AnotherClient);
    }

    [Fact]
    public async Task ATreatmentWrittenLiveWhileWriteBackWasOff_DeletedWithItOn_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        await CreateAsync(upstream, Bolus(legacyId, slot), writeBackEnabled: false);
        Hold(upstream, legacyId, slot);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    [Fact]
    public async Task ATreatmentWhoseSendUpstreamRefused_DeletedByItsId_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        await CreateAsync(new Refusing(), Bolus(legacyId, slot));
        var upstream = Upstream(slot);
        Hold(upstream, legacyId, slot);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    [Fact]
    public async Task ATreatmentOnlyImported_DeletedByItsId_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        Hold(upstream, legacyId, slot);
        await WithWriteBackAsync(upstream, async s =>
        {
            (await s.ImportTreatmentsAsync([Bolus(legacyId, slot)])).CreatedRecords.Should().NotBeEmpty();
            return true;
        });

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    /// <summary>
    /// A record a migration imported, then edited in Nocturne, was written upstream by that edit onto
    /// the uploader's document. Once upstream accepts the edit the record counts as sent, so its delete
    /// removes that document where it holds the legacy id as its <c>identifier</c>. A document with no
    /// <c>identifier</c>, as a v1 upload leaves it, is not found by the delete and stays.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnImportedTreatmentEditedAndWrittenBack_DeletedByItsId_LosesTheCopyHeldUnderItsId(bool heldUnderIdentifier)
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        var original = new JsonObject
        {
            ["_id"] = legacyId, ["eventType"] = "Correction Bolus", ["insulin"] = 0.7, ["created_at"] = At(slot),
        };
        if (heldUnderIdentifier)
            original["identifier"] = legacyId;
        upstream.Seed(original, asObjectId: true);
        await WithWriteBackAsync(upstream, async s =>
        {
            (await s.ImportTreatmentsAsync([Bolus(legacyId, slot)])).CreatedRecords.Should().NotBeEmpty();
            var edit = Bolus(legacyId, slot);
            edit.Insulin = 1.2;
            (await s.UpdateTreatmentAsync(legacyId, edit)).Should().NotBeNull();
            return true;
        });
        upstream.Documents.Should().HaveCount(2);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Identifiers(upstream).Should().BeEquivalentTo(heldUnderIdentifier ? [AnotherClient] : [null, AnotherClient]);
    }

    [Fact]
    public async Task ATreatmentUpstreamAccepted_DeletedByAFilter_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        await CreateAsync(upstream, Bolus(legacyId, slot));

        var deleted = await WithWriteBackAsync(upstream, s => s.DeleteTreatmentsAsync(
            $"find[eventType]=Correction Bolus&find[created_at][$gte]={At(slot)}&find[created_at][$lte]={At(slot)}"));

        deleted.Should().Be(1);
        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    [Fact]
    public async Task AMealWhoseHalvesUpstreamEachAccepted_DeletedByItsId_LosesBothCopiesUpstream()
    {
        var slot = UniqueSlot();
        var (bolusId, carbsId) = (MongoObjectId.NewObjectId(), MongoObjectId.NewObjectId());
        var meal = await StoreMealAsync(slot, bolusId, carbsId);
        await UpstreamAcceptedAsync(bolusId, carbsId);
        var upstream = Upstream(slot);
        Hold(upstream, bolusId, slot);
        Hold(upstream, carbsId, slot.AddMinutes(1));

        (await DeleteAsync(upstream, MongoObjectId.FromGuid(meal))).Should().BeTrue();

        Identifiers(upstream).Should().Equal(AnotherClient);
    }

    [Fact]
    public async Task AMealOfWhichUpstreamAcceptedOnlyTheCarbs_DeletedByItsId_KeepsTheBolusCopyUpstream()
    {
        var slot = UniqueSlot();
        var (bolusId, carbsId) = (MongoObjectId.NewObjectId(), MongoObjectId.NewObjectId());
        var meal = await StoreMealAsync(slot, bolusId, carbsId);
        await UpstreamAcceptedAsync(carbsId);
        var upstream = Upstream(slot);
        Hold(upstream, bolusId, slot);
        Hold(upstream, carbsId, slot.AddMinutes(1));

        (await DeleteAsync(upstream, MongoObjectId.FromGuid(meal))).Should().BeTrue();

        Identifiers(upstream).Should().BeEquivalentTo([bolusId, AnotherClient]);
    }

    /// <summary>
    /// The copy the first record left upstream was removed with it. What upstream holds under the id
    /// after that is not one write-back wrote, though the first record's tombstone still holds its send.
    /// </summary>
    [Fact]
    public async Task ATreatmentRecreatedUnderTheIdOfOneUpstreamAccepted_NeverSentItself_DeletedByItsId_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        await CreateAsync(upstream, Bolus(legacyId, slot));
        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();
        Deletes(upstream).Should().Be(1);
        await CreateAsync(upstream, Bolus(legacyId, slot), writeBackEnabled: false);
        Hold(upstream, legacyId, slot);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Deletes(upstream).Should().Be(1);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    [Fact]
    public async Task ATreatmentNeverSent_WhoseIdANoteUpstreamAcceptedHolds_DeletedByItsId_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<INoteRepository>().CreateAsync(new Note
            {
                Timestamp = slot.UtcDateTime, Text = "sent", LegacyId = legacyId, DataSource = "careportal",
            }, WriteOrigin.Live);
        }
        await UpstreamAcceptedAsync(legacyId);
        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>().CreateAsync(new Bolus
            {
                Timestamp = slot.UtcDateTime, Insulin = 1, LegacyId = legacyId, DataSource = "careportal",
            }, WriteOrigin.Live);
        }
        var upstream = Upstream(slot);
        Hold(upstream, legacyId, slot);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    [Fact]
    public async Task AMealWhoseCarbsTheNightscoutConnectorPulled_DeletedByItsId_KeepsTheCarbsCopyUpstream()
    {
        var slot = UniqueSlot();
        var (bolusId, carbsId) = (MongoObjectId.NewObjectId(), MongoObjectId.NewObjectId());
        var meal = await StoreMealAsync(slot, bolusId, carbsId, carbsSource: DataSources.NightscoutConnector);
        await UpstreamAcceptedAsync(carbsId);
        var upstream = Upstream(slot);
        Hold(upstream, bolusId, slot);
        Hold(upstream, carbsId, slot.AddMinutes(1));

        (await DeleteAsync(upstream, MongoObjectId.FromGuid(meal))).Should().BeTrue();

        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([bolusId, carbsId, AnotherClient]);
    }

    [Fact]
    public async Task ARecordASpanHoldsTheIdOf_DeletedByThatId_SendsNothingUpstream()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = Upstream(slot);
        await CreateAsync(upstream, new Treatment
        {
            Id = legacyId, EventType = "Temporary Override", Duration = 60, Reason = "Exercise", CreatedAt = At(slot),
        }, writeBackEnabled: false);
        await using (var scope = OpenTenantScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBolusRepository>().CreateAsync(new Bolus
            {
                Timestamp = slot.UtcDateTime, Insulin = 1, LegacyId = legacyId, DataSource = "careportal",
            }, WriteOrigin.Live);
        }
        await UpstreamAcceptedAsync(legacyId);
        Hold(upstream, legacyId, slot);

        (await DeleteAsync(upstream, legacyId)).Should().BeTrue();

        Deletes(upstream).Should().Be(0);
        Identifiers(upstream).Should().BeEquivalentTo([legacyId, AnotherClient]);
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("refused") });
    }
}
