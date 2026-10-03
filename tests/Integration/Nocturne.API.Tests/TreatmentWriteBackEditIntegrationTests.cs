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
}
