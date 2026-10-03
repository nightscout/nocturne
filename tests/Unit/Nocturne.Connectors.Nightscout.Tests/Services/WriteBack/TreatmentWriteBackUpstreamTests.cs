using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// Treatment write-back against the upstream's own storage rules (<see cref="FakeNightscoutTreatments"/>):
/// an edit made in Nocturne lands on the one copy upstream, whether an earlier write-back left it
/// there in the shape it always sent (both <c>_id</c> and <c>identifier</c> carrying the coerced
/// key) or the upstream holds the treatment's original under its ObjectId. A second copy would give
/// every follower reading that Nightscout, and an AAPS syncing from it, the dose twice.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentWriteBackUpstreamTests
{
    private const string At = "2026-09-01T08:00:00.000Z";
    private const string RecordUuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";

    private static NightscoutTreatmentWriteBackSink Sink(FakeNightscoutTreatments upstream)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret-12345",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50,
        });
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream), loader.Object, new NightscoutCircuitBreaker(), NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    /// <summary>What write-back sent before <c>UpstreamIdentityJson</c>: the treatment through the web defaults.</summary>
    private static async Task EarlierWriteBackAsync(FakeNightscoutTreatments upstream, HttpMethod method, Treatment treatment)
    {
        using var client = new HttpClient(upstream);
        using var request = new HttpRequestMessage(method, "https://nightscout.example.com/api/v1/treatments")
        {
            Content = method == HttpMethod.Post ? JsonContent.Create(new[] { treatment }) : JsonContent.Create(treatment),
        };
        (await client.SendAsync(request)).IsSuccessStatusCode.Should().BeTrue();
    }

    private static Treatment Uploaded(string id, double insulin = 1) =>
        new() { Id = id, EventType = "Correction Bolus", Insulin = insulin, CreatedAt = At, DataSource = "aaps" };

    /// <summary>An edit as <c>TreatmentService</c> raises it: the record's projection, served by its uuid.</summary>
    private static Treatment Edited(string? legacyId, double insulin, string at = At) =>
        new() { Id = RecordUuid, LegacyId = legacyId, EventType = "Correction Bolus", Insulin = insulin, CreatedAt = at, DataSource = "aaps" };

    private static IEnumerable<(string? Identifier, double Insulin)> Copies(FakeNightscoutTreatments upstream) =>
        upstream.Documents.Select(d => ((string?)d.Document["identifier"], (double)d.Document["insulin"]!));

    public static TheoryData<string> Keys => new()
    {
        "65a1b2c3d4e5f60718293a4b",
        "syn-3a7c0e9f1b2d4c6e",
        "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f",
    };

    /// <summary>
    /// Nightscout narrows a treatment find that bounds no <c>created_at</c> to the last four days, so
    /// asking for the copy by its identifier alone misses the copy of an older treatment, and the
    /// edit PUT under its <c>_id</c> alone lands on neither copy: 15.0.8 stores a second one. The
    /// lookup bounds <c>created_at</c> itself, including below a time the edit moved.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keys))]
    public async Task On1508_AnEditOfATreatmentOlderThanFourDaysLandsOnTheOneCopy(string key)
    {
        var upstream = new FakeNightscoutTreatments("15.0.8") { Now = DateTimeOffset.Parse(At).AddDays(30) };
        await EarlierWriteBackAsync(upstream, HttpMethod.Post, Uploaded(key));
        using (var client = new HttpClient(upstream))
        {
            (await client.GetStringAsync($"https://nightscout.example.com/api/v1/treatments.json?find[identifier]={MongoObjectId.Coerce(key)}&count=1"))
                .Should().Be("[]", "a find with no created_at bound only sees the last four days");
        }

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));
        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 3, at: "2026-08-20T08:00:00.000Z"));

        Copies(upstream).Should().Equal((MongoObjectId.Coerce(key), 3d));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task On1508_AnEditLandsOnTheCopyAnEarlierWriteBackCreated(string key)
    {
        var upstream = new FakeNightscoutTreatments("15.0.8");
        await EarlierWriteBackAsync(upstream, HttpMethod.Post, Uploaded(key));

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));

        Copies(upstream).Should().Equal((MongoObjectId.Coerce(key), 2d));
    }

    /// <summary>
    /// A v4-native treatment an earlier write-back sent on its first v1 edit went up under its uuid's
    /// prefix, which that edit also gave it as its legacy id.
    /// </summary>
    [Fact]
    public async Task On1508_AnEditLandsOnTheCopyAnEarlierWriteBackOfAnEditCreated()
    {
        var upstream = new FakeNightscoutTreatments("15.0.8");
        var prefix = MongoObjectId.FromGuid(Guid.Parse(RecordUuid));
        await EarlierWriteBackAsync(upstream, HttpMethod.Put, Edited(legacyId: null, insulin: 1));

        await Sink(upstream).OnUpdatedAsync(Edited(prefix, insulin: 2));

        Copies(upstream).Should().Equal((prefix, 2d));
    }

    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnEditOfATreatmentUpstreamHoldsUnderItsObjectIdAlone_LandsOnIt(string version)
    {
        const string key = "65a1b2c3d4e5f60718293a4b";
        var upstream = new FakeNightscoutTreatments(version);
        upstream.Seed(new JsonObject { ["_id"] = key, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At }, asObjectId: true);

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));

        upstream.Documents.Select(d => (d.Id, d.IsObjectId, (double)d.Document["insulin"]!)).Should().Equal((key, true, 2d));
        upstream.Documents.Single().Document.ContainsKey("identifier").Should().BeFalse();
    }

    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnEditOfATreatmentUpstreamHoldsNowhere_IsStoredOnce(string version)
    {
        var upstream = new FakeNightscoutTreatments(version);

        await Sink(upstream).OnUpdatedAsync(Edited("syn-3a7c0e9f1b2d4c6e", insulin: 2));
        await Sink(upstream).OnUpdatedAsync(Edited("syn-3a7c0e9f1b2d4c6e", insulin: 3));

        upstream.Documents.Select(d => (double)d.Document["insulin"]!).Should().Equal(3d);
    }

    /// <summary>
    /// Up to 15.0.6 a POST stores a treatment under its <c>_id</c> as a string, and a PUT saves
    /// under <c>new ObjectID(_id)</c>, which never equals it: the PUT earlier write-backs sent for
    /// an edit stored the dose a second time. An edit POSTed finds the copy by time and event type.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keys))]
    public async Task On1506_AnEditLandsOnTheCopyAnEarlierWriteBackCreated_WhereAPutWouldDuplicateIt(string key)
    {
        var upstream = new FakeNightscoutTreatments("15.0.6");
        await EarlierWriteBackAsync(upstream, HttpMethod.Post, Uploaded(key));

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));

        Copies(upstream).Should().Equal((MongoObjectId.Coerce(key), 2d));
        upstream.Documents.Single().IsObjectId.Should().BeFalse();

        var before = new FakeNightscoutTreatments("15.0.6");
        await EarlierWriteBackAsync(before, HttpMethod.Post, Uploaded(key));
        await EarlierWriteBackAsync(before, HttpMethod.Put, Uploaded(key, insulin: 2));
        before.Documents.Should().HaveCount(2, "the PUT of an edit stored a second copy");
    }

    /// <summary>
    /// An edit that moves the treatment's time or event type finds no copy by them up to 15.0.6, and
    /// inserting it under the <c>_id</c> the copy holds is refused: the edit does not reach the
    /// upstream, and no second copy is stored.
    /// </summary>
    [Fact]
    public async Task On1506_AnEditThatMovesTheTreatmentIsRefusedRatherThanStoredTwice()
    {
        const string key = "65a1b2c3d4e5f60718293a4b";
        var upstream = new FakeNightscoutTreatments("15.0.6");
        await EarlierWriteBackAsync(upstream, HttpMethod.Post, Uploaded(key));

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2, at: "2026-09-01T08:05:00.000Z"));

        Copies(upstream).Should().Equal((key, 1d));
        upstream.Refusals.Should().Be(1);
    }
}
