using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// An edit of a stored treatment, raised by <see cref="TreatmentService"/> against Postgres and
/// written back through <see cref="NightscoutTreatmentWriteBackSink"/> to a model of a 15.0.8
/// Nightscout (<see cref="FakeNightscoutTreatments"/>), leaves one copy of the dose upstream (#1804).
/// Every treatment here is years old: Nightscout's treatment finds default to the last four days.
/// </summary>
[Trait("Category", "Integration")]
public class TreatmentWriteBackEditIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string UpstreamUrl = "https://nightscout.example.com";

    /// <summary>A slot no other test writes a bolus into, since the fixture's tenant is shared.</summary>
    private static DateTimeOffset UniqueSlot() =>
        new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 150_000) * 20);

    private static string At(DateTimeOffset slot) => slot.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>Runs <paramref name="act"/> on the tenant's treatment service, writing back to <paramref name="upstream"/>.</summary>
    private async Task WithWriteBackAsync(FakeNightscoutTreatments upstream, Func<ITreatmentService, Task> act)
    {
        using var scope = Fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        services.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;

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
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        await act(new TreatmentService(
            services.GetRequiredService<ITreatmentStore>(),
            services.GetRequiredService<ITreatmentDecomposer>(),
            services.GetRequiredService<ITreatmentCache>(),
            sink,
            services.GetRequiredService<IPatientInsulinRepository>(),
            services.GetRequiredService<ILogger<TreatmentService>>()));
    }

    private async Task<List<(Guid Id, double Insulin)>> LiveBolusesAsync(DateTimeOffset slot)
    {
        var from = Uri.EscapeDataString(slot.AddMinutes(-1).ToString("O"));
        var to = Uri.EscapeDataString(slot.AddMinutes(1).ToString("O"));
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v4/insulin/boluses?limit=50&from={from}&to={to}");
        return body.GetProperty("data").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetGuid(), r.GetProperty("insulin").GetDouble()))
            .ToList();
    }

    private static IEnumerable<(string? Identifier, double Insulin)> Copies(FakeNightscoutTreatments upstream) =>
        upstream.Documents.Select(d => ((string?)d.Document["identifier"], (double)d.Document["insulin"]!));

    public static TheoryData<string> KeyShapes => new() { "objectid", "uuid", "other" };

    private static string NewKey(string shape) => shape switch
    {
        "objectid" => MongoObjectId.NewObjectId(),
        "uuid" => Guid.NewGuid().ToString(),
        _ => $"integration-tr-{Guid.NewGuid():N}",
    };

    /// <summary>
    /// A treatment uploaded through v1 is answered with the id reads serve it by, the id a client
    /// edits it by, and an earlier write-back left its copy upstream under the coercion of its key as
    /// both <c>_id</c> and <c>identifier</c>. A v1 edit of it, years later, lands on that copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(KeyShapes))]
    public async Task AnEditOfATreatmentOlderThanFourDays_LandsOnItsOneCopyUpstream(string shape)
    {
        var slot = UniqueSlot();
        var legacyId = NewKey(shape);
        var wire = MongoObjectId.Coerce(legacyId)!;
        var upload = new { _id = legacyId, eventType = "Correction Bolus", insulin = 0.7, created_at = At(slot) };

        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[] { upload });
        created.IsSuccessStatusCode.Should().BeTrue();
        var bolus = (await LiveBolusesAsync(slot)).Single().Id;
        (await created.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(bolus));

        var upstream = new FakeNightscoutTreatments("15.0.8");
        using (var client = new HttpClient(upstream, disposeHandler: false))
        {
            (await client.PostAsJsonAsync($"{UpstreamUrl}/api/v1/treatments", new[]
            {
                new JsonObject { ["_id"] = wire, ["identifier"] = wire, ["eventType"] = "Correction Bolus", ["insulin"] = 0.7, ["created_at"] = At(slot) },
            })).IsSuccessStatusCode.Should().BeTrue();
        }

        await WithWriteBackAsync(upstream, service => service.UpdateTreatmentAsync(
            MongoObjectId.FromGuid(bolus),
            new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 1.1, CreatedAt = At(slot) }));

        Copies(upstream).Should().Equal((wire, 1.1));
        (await LiveBolusesAsync(slot)).Should().Equal((bolus, 1.1));
    }

    /// <summary>
    /// A client that created a treatment by its ObjectId PATCHes it by that id, the one the create
    /// answered with. The edit goes upstream under the key the create went under, onto its copy.
    /// </summary>
    [Fact]
    public async Task APatchByTheRawLegacyObjectId_IsWrittenBackUnderTheKeyTheCreateWasWrittenBackUnder()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upstream = new FakeNightscoutTreatments("15.0.8");

        await WithWriteBackAsync(upstream, service => service.CreateTreatmentsAsync(
            [new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 0.7, CreatedAt = At(slot) }]));
        var createdUnder = (string?)upstream.Documents.Single().Document["identifier"];
        createdUnder.Should().Be(legacyId);
        var bolus = (await LiveBolusesAsync(slot)).Single().Id;

        await WithWriteBackAsync(upstream, service => service.PatchTreatmentAsync(
            legacyId, JsonSerializer.Deserialize<JsonElement>("""{"insulin":1.3}""")));

        Copies(upstream).Should().Equal((createdUnder, 1.3));
        (await LiveBolusesAsync(slot)).Should().Equal((bolus, 1.3));
    }

    private async Task<Guid> LiveTempBasalAsync(DateTimeOffset slot)
    {
        var from = Uri.EscapeDataString(slot.AddMinutes(-1).UtcDateTime.ToString("O"));
        var to = Uri.EscapeDataString(slot.AddMinutes(1).UtcDateTime.ToString("O"));
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v4/insulin/temp-basals?limit=50&from={from}&to={to}");
        return body.GetProperty("data").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static async Task PostUpstreamAsync(FakeNightscoutTreatments upstream, JsonObject doc)
    {
        using var client = new HttpClient(upstream, disposeHandler: false);
        (await client.PostAsJsonAsync($"{UpstreamUrl}/api/v1/treatments", new JsonArray(doc))).IsSuccessStatusCode.Should().BeTrue();
    }

    /// <summary>
    /// The identifier each release left a treatment's copy under, given its legacy id and its
    /// record's uuid: the raw key (v0.0.1 to v0.2.3), the record's uuid prefix (temp basals with a
    /// legacy id that is not an ObjectId from v0.2.4 to v0.2.7, every create on main after #1960),
    /// and the coerced key (this release).
    /// </summary>
    private static string SentUnder(string release, string legacyId, Guid recordId) => release switch
    {
        "v0.2.3" => legacyId,
        "v0.2.7" or "#1960" => MongoObjectId.FromGuid(recordId),
        _ => MongoObjectId.Coerce(legacyId)!,
    };

    /// <summary>
    /// A temp basal uploaded through v1 with a legacy id that is not an ObjectId, written back by an
    /// earlier release under the form that release used, is shortened in Nocturne (as AAPS cancels
    /// one early). The edit lands on that copy, on 15.0.8 and 15.0.6, and leaves one temp basal.
    /// </summary>
    [Theory]
    [InlineData("15.0.8", "v0.2.3")]
    [InlineData("15.0.8", "v0.2.7")]
    [InlineData("15.0.8", "current")]
    [InlineData("15.0.6", "v0.2.3")]
    [InlineData("15.0.6", "v0.2.7")]
    [InlineData("15.0.6", "current")]
    public async Task AnEditOfATempBasal_LandsOnTheCopyTheReleaseThatWroteItBackLeft(string version, string release)
    {
        var slot = UniqueSlot();
        var legacyId = $"integration-tb-{Guid.NewGuid():N}";
        var upload = new { _id = legacyId, eventType = "Temp Basal", duration = 30, absolute = 1.2, rate = 1.2, created_at = At(slot) };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[] { upload })).IsSuccessStatusCode.Should().BeTrue();
        var tempBasal = await LiveTempBasalAsync(slot);

        var sentUnder = SentUnder(release, legacyId, tempBasal);
        var upstream = new FakeNightscoutTreatments(version);
        await PostUpstreamAsync(upstream, new JsonObject
        {
            ["_id"] = sentUnder, ["identifier"] = sentUnder, ["eventType"] = "Temp Basal", ["duration"] = 30.0, ["absolute"] = 1.2, ["created_at"] = At(slot),
        });

        await WithWriteBackAsync(upstream, service => service.UpdateTreatmentAsync(
            tempBasal.ToString(),
            new Treatment { Id = legacyId, EventType = "Temp Basal", Duration = 12, Absolute = 1.2, Rate = 1.2, CreatedAt = At(slot) }));

        upstream.Documents.Select(d => ((string?)d.Document["identifier"], (double)d.Document["duration"]!)).Should().Equal((sentUnder, 12d));
        upstream.Refusals.Should().Be(0);
        (await LiveTempBasalAsync(slot)).Should().Be(tempBasal);
    }

    /// <summary>
    /// A correction bolus an earlier release wrote back under its raw key (v0.0.1 to v0.2.3) or its
    /// record's uuid prefix (main after #1960) is edited in Nocturne: the edit lands on that copy.
    /// </summary>
    [Theory]
    [InlineData("15.0.8", "v0.2.3")]
    [InlineData("15.0.8", "#1960")]
    [InlineData("15.0.6", "v0.2.3")]
    [InlineData("15.0.6", "#1960")]
    public async Task AnEditOfABolus_LandsOnTheCopyTheReleaseThatWroteItBackLeft(string version, string release)
    {
        var slot = UniqueSlot();
        var legacyId = $"integration-tr-{Guid.NewGuid():N}";
        var upload = new { _id = legacyId, eventType = "Correction Bolus", insulin = 0.7, created_at = At(slot) };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[] { upload })).IsSuccessStatusCode.Should().BeTrue();
        var bolus = (await LiveBolusesAsync(slot)).Single().Id;

        var sentUnder = SentUnder(release, legacyId, bolus);
        var upstream = new FakeNightscoutTreatments(version);
        await PostUpstreamAsync(upstream, new JsonObject
        {
            ["_id"] = sentUnder, ["identifier"] = sentUnder, ["eventType"] = "Correction Bolus", ["insulin"] = 0.7, ["created_at"] = At(slot),
        });

        await WithWriteBackAsync(upstream, service => service.PatchTreatmentAsync(
            MongoObjectId.FromGuid(bolus), JsonSerializer.Deserialize<JsonElement>("""{"insulin":1.4}""")));

        Copies(upstream).Should().Equal((sentUnder, 1.4));
        upstream.Refusals.Should().Be(0);
        (await LiveBolusesAsync(slot)).Should().Equal((bolus, 1.4));
    }

    /// <summary>
    /// A treatment edited while upstream held no copy of it, then uploaded again (a client's resend),
    /// leaves one copy: the edit went up as a create does, under both keys, so the create's identifier
    /// upsert lands on it.
    /// </summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task ATreatmentEditedWhileNothingWasUpstream_ThenUploadedAgain_LeavesOneCopy(string version)
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { _id = legacyId, eventType = "Correction Bolus", insulin = 0.7, created_at = At(slot) },
        })).IsSuccessStatusCode.Should().BeTrue();
        var bolus = (await LiveBolusesAsync(slot)).Single().Id;
        var upstream = new FakeNightscoutTreatments(version);

        await WithWriteBackAsync(upstream, service => service.UpdateTreatmentAsync(
            MongoObjectId.FromGuid(bolus),
            new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 1.1, CreatedAt = At(slot) }));
        await WithWriteBackAsync(upstream, service => service.CreateTreatmentsAsync(
            [new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 1.1, CreatedAt = At(slot) }]));

        Copies(upstream).Should().Equal((legacyId, 1.1));
        (await LiveBolusesAsync(slot)).Should().Equal((bolus, 1.1));
    }

    /// <summary>
    /// A treatment the user deleted, uploaded again, is not stored, and so is not written back:
    /// upstream would otherwise hold a dose Nocturne does not.
    /// </summary>
    [Fact]
    public async Task ACreateTheUsersDeletionWithholds_IsNotWrittenBack()
    {
        var slot = UniqueSlot();
        var legacyId = MongoObjectId.NewObjectId();
        var upload = new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 0.7, CreatedAt = At(slot) };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { _id = legacyId, eventType = "Correction Bolus", insulin = 0.7, created_at = At(slot) },
        })).IsSuccessStatusCode.Should().BeTrue();
        var bolus = (await LiveBolusesAsync(slot)).Single().Id;
        (await AuthenticatedClient.DeleteAsync($"/api/v1/treatments/{MongoObjectId.FromGuid(bolus)}")).IsSuccessStatusCode.Should().BeTrue();
        var upstream = new FakeNightscoutTreatments("15.0.8");

        await WithWriteBackAsync(upstream, service => service.CreateTreatmentsAsync([upload]));

        upstream.Writes.Should().BeEmpty();
        (await LiveBolusesAsync(slot)).Should().BeEmpty();
    }
}
