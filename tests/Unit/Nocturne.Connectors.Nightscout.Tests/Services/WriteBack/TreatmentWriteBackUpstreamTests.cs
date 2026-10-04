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
        new() { Id = RecordUuid, LegacyId = legacyId, RecordId = Guid.Parse(RecordUuid), EventType = "Correction Bolus", Insulin = insulin, CreatedAt = at, DataSource = "aaps" };

    /// <summary>A create as <c>TreatmentService</c> raises it: served by its record's uuid, keyed by its legacy id.</summary>
    private static Treatment Created(string legacyId, double insulin) =>
        new() { Id = RecordUuid, LegacyId = legacyId, RecordId = Guid.Parse(RecordUuid), EventType = "Correction Bolus", Insulin = insulin, CreatedAt = At, DataSource = "aaps" };

    /// <summary>A temp basal's edit as <c>TempBasalToTreatmentMapper</c> projects it.</summary>
    private static Treatment TempBasal(string? legacyId, double rate) => new()
    {
        Id = MongoObjectId.IsObjectId(legacyId) ? legacyId : RecordUuid,
        LegacyId = legacyId,
        RecordId = Guid.Parse(RecordUuid),
        EventType = "Temp Basal",
        Duration = 30,
        Absolute = rate,
        Rate = rate,
        Temp = "absolute",
        CreatedAt = At,
        DataSource = "aaps",
    };

    private static readonly string RecordPrefix = MongoObjectId.FromGuid(Guid.Parse(RecordUuid));

    /// <summary>Writes <paramref name="doc"/> upstream as an earlier write-back sent it, by POST.</summary>
    private static async Task EarlierPostAsync(FakeNightscoutTreatments upstream, JsonObject doc)
    {
        using var client = new HttpClient(upstream);
        (await client.PostAsJsonAsync("https://nightscout.example.com/api/v1/treatments", new JsonArray(doc))).IsSuccessStatusCode.Should().BeTrue();
    }

    private static IEnumerable<(string? Identifier, double Value)> Copies(FakeNightscoutTreatments upstream, string field) =>
        upstream.Documents.Select(d => ((string?)d.Document["identifier"], (double)d.Document[field]!));

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
    /// An edit of a treatment upstream holds nowhere is sent as a create is, under both keys. Sent
    /// under its <c>_id</c> alone, 15.0.7 and later would store it under that ObjectId with no
    /// identifier, and the next create of the treatment (a client re-upload, the v1 PUT create
    /// fallback, a connector republish), upserted by identifier, would never match it: a second copy.
    /// </summary>
    [Theory]
    [InlineData("15.0.8", "65a1b2c3d4e5f60718293a4b")]
    [InlineData("15.0.8", "syn-3a7c0e9f1b2d4c6e")]
    [InlineData("15.0.8", "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f")]
    [InlineData("15.0.6", "65a1b2c3d4e5f60718293a4b")]
    [InlineData("15.0.6", "syn-3a7c0e9f1b2d4c6e")]
    [InlineData("15.0.6", "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f")]
    public async Task ATreatmentEditedWhileNothingWasUpstream_ThenCreatedAgain_LeavesOneCopy(string version, string key)
    {
        var upstream = new FakeNightscoutTreatments(version);

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));
        await Sink(upstream).OnCreatedAsync([Created(key, insulin: 3)]);
        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 4));

        Copies(upstream).Should().Equal((MongoObjectId.Coerce(key), 4d));

        var idOnly = new FakeNightscoutTreatments("15.0.8");
        using (var client = new HttpClient(idOnly))
        {
            (await client.PutAsJsonAsync("https://nightscout.example.com/api/v1/treatments", new JsonObject
            {
                ["_id"] = MongoObjectId.Coerce(key), ["eventType"] = "Correction Bolus", ["insulin"] = 2.0, ["created_at"] = At,
            })).IsSuccessStatusCode.Should().BeTrue();
        }

        idOnly.Documents.Single().IsObjectId.Should().BeTrue();
        await Sink(idOnly).OnCreatedAsync([Created(key, insulin: 3)]);
        idOnly.Documents.Should().HaveCount(2, "a create never matches a copy 15.0.8 holds under its ObjectId alone");
    }

    /// <summary>
    /// A copy each earlier release left upstream, under the identifier it sent, which this release
    /// does not create under: the raw key (v0.0.1 to v0.2.3), the record's uuid prefix for a temp
    /// basal whose legacy id was not an ObjectId (v0.2.4 to v0.2.7) and for any create (main after
    /// #1960). <c>_id</c> was sent equal to it.
    /// </summary>
    public static TheoryData<string, string, string, string> EarlierCopies
    {
        get
        {
            var data = new TheoryData<string, string, string, string>();
            foreach (var version in new[] { "15.0.8", "15.0.6" })
            {
                data.Add(version, "v0.2.3 raw key", "Correction Bolus", "syn-3a7c0e9f1b2d4c6e");
                data.Add(version, "v0.2.3 raw uuid key", "Correction Bolus", "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f");
                data.Add(version, "v0.2.3 raw temp basal key", "Temp Basal", "syn-tb-3a7c0e9f");
                data.Add(version, "v0.2.7 temp basal uuid prefix", "Temp Basal", "syn-tb-3a7c0e9f");
                data.Add(version, "v0.2.7 temp basal uuid prefix, uuid key", "Temp Basal", "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f");
                data.Add(version, "main #1960 uuid prefix", "Correction Bolus", "syn-3a7c0e9f1b2d4c6e");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EarlierCopies))]
    public async Task AnEditLandsOnTheCopyAnEarlierReleaseLeftUnderAnotherIdentifier(string version, string release, string eventType, string legacyId)
    {
        var upstream = new FakeNightscoutTreatments(version);
        var sentUnder = release.Contains("raw", StringComparison.Ordinal) ? legacyId : RecordPrefix;
        var tempBasal = eventType == "Temp Basal";
        var field = tempBasal ? "absolute" : "insulin";
        await EarlierPostAsync(upstream, new JsonObject
        {
            ["_id"] = sentUnder, ["identifier"] = sentUnder, ["eventType"] = eventType, [field] = 1.0, ["created_at"] = At,
        });

        var edit = tempBasal ? TempBasal(legacyId, rate: 2) : Edited(legacyId, insulin: 2);
        await Sink(upstream).OnUpdatedAsync(edit);
        await Sink(upstream).OnUpdatedAsync(tempBasal ? TempBasal(legacyId, rate: 3) : Edited(legacyId, insulin: 3));

        Copies(upstream, field).Should().Equal((sentUnder, 3d));
        upstream.Refusals.Should().Be(0);
    }

    /// <summary>
    /// Up to 15.0.6 the PUT an earlier write-back sent for an edit of a treatment held nowhere saved
    /// it under <c>new ObjectID(_id)</c>, with the identifier. A read serves that ObjectId as the
    /// identifier's own string, so the edit asks whether it is one before writing: a POST under the
    /// string would be refused, since it differs from the ObjectId the copy is matched by.
    /// </summary>
    [Fact]
    public async Task On1506_AnEditLandsOnTheCopyAnEarlierPutSavedUnderAnObjectId()
    {
        const string key = "65a1b2c3d4e5f60718293a4b";
        var upstream = new FakeNightscoutTreatments("15.0.6");
        await EarlierWriteBackAsync(upstream, HttpMethod.Put, Uploaded(key));
        upstream.Documents.Single().IsObjectId.Should().BeTrue();

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2, at: "2026-09-01T08:05:00.000Z"));

        upstream.Documents.Select(d => (d.Id, d.IsObjectId, (string?)d.Document["identifier"], (double)d.Document["insulin"]!))
            .Should().Equal((key, true, key, 2d));
        upstream.Refusals.Should().Be(0);
    }

    /// <summary>
    /// A treatment a v3 client such as AAPS uploaded is held under an ObjectId, with that client's
    /// own identifier, which AAPS matches the record by. Imported by a Nightscout migration under its
    /// ObjectId, it has no copy under any identifier write-back uses, and the edit is PUT under the
    /// ObjectId. A PUT replaces the whole document, so the edit carries that identifier on.
    /// </summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnEditOfATreatmentAV3ClientUploaded_KeepsThatClientsIdentifier(string version)
    {
        const string key = "65a1b2c3d4e5f60718293a4b";
        const string aapsIdentifier = "a3c1f2e4-5b6d-4e7f-8a9b-0c1d2e3f4a5b";
        var upstream = new FakeNightscoutTreatments(version);
        upstream.Seed(new JsonObject
        {
            ["_id"] = key, ["identifier"] = aapsIdentifier, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At,
        }, asObjectId: true);

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));

        upstream.Documents.Select(d => (d.Id, d.IsObjectId, (string?)d.Document["identifier"], (double)d.Document["insulin"]!))
            .Should().Equal((key, true, aapsIdentifier, 2d));
    }

    /// <summary>
    /// A temp basal keyed by an ObjectId went upstream under it in every release; an edit, shortening
    /// it as AAPS does when it cancels one early, lands on that copy.
    /// </summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnEditOfATempBasalLandsOnItsOneCopy(string version)
    {
        const string key = "65a1b2c3d4e5f60718293a4b";
        var upstream = new FakeNightscoutTreatments(version);
        await Sink(upstream).OnCreatedAsync([TempBasal(key, rate: 1.2)]);

        var shortened = TempBasal(key, rate: 1.2);
        shortened.Duration = 12;
        await Sink(upstream).OnUpdatedAsync(shortened);

        upstream.Documents.Select(d => ((string?)d.Document["identifier"], (double)d.Document["duration"]!)).Should().Equal((key, 12d));
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
    /// The original of a treatment a Nightscout migration imported from an uploader that sent its own
    /// id: held under that id as a string <c>_id</c>, written before 15.0.7 normalised such ids, with
    /// no identifier. An edit sent under the create's keys missed it: up to 15.0.6 the POST matched it
    /// by time and event type and was refused for changing its <c>_id</c>, on every edit; from 15.0.7
    /// it matched neither arm of the identifier's <c>$or</c> and stored a second copy. Found under
    /// its string <c>_id</c>, the edit lands on it.
    /// </summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task AnEditOfAnOriginalHeldUnderAStringIdWithNoIdentifier_LandsOnIt(string version)
    {
        const string legacyId = "xdrip-3a7c0e9f1b2d4c6e";
        var upstream = new FakeNightscoutTreatments(version) { Now = DateTimeOffset.Parse(At).AddDays(30) };
        upstream.Seed(new JsonObject { ["_id"] = legacyId, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At }, asObjectId: false);

        await Sink(upstream).OnUpdatedAsync(Edited(legacyId, insulin: 2));
        await Sink(upstream).OnUpdatedAsync(Edited(legacyId, insulin: 3));

        upstream.Documents.Select(d => (d.Id, d.IsObjectId, (string?)d.Document["identifier"], (double)d.Document["insulin"]!))
            .Should().Equal((legacyId, false, legacyId, 3d));
        upstream.Refusals.Should().Be(0);
    }

    /// <summary>
    /// What the create's keys do to that original: up to 15.0.6 a refusal on every edit, which counts
    /// against the circuit breaker every sink shares; from 15.0.7 a second copy.
    /// </summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task TheCreatesKeys_MissAnOriginalHeldUnderAStringId(string version)
    {
        const string legacyId = "xdrip-3a7c0e9f1b2d4c6e";
        var upstream = new FakeNightscoutTreatments(version);
        upstream.Seed(new JsonObject { ["_id"] = legacyId, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At }, asObjectId: false);

        await Sink(upstream).OnCreatedAsync([Created(legacyId, insulin: 2)]);

        (upstream.Documents.Count, upstream.Refusals).Should().Be(version == "15.0.8" ? (2, 0) : (1, 1));
    }

    /// <summary>
    /// Up to v0.2.3 an edit went upstream as the projection the update read back, served by the
    /// record's full uuid, under it as both keys whatever the legacy id. 15.0.7 and later kept the
    /// uuid as the identifier of a copy under a minted ObjectId; up to 15.0.6 <c>save()</c> threw on
    /// it and stored nothing. A later edit lands on that copy.
    /// </summary>
    [Theory]
    [InlineData("15.0.8", "65a1b2c3d4e5f60718293a4b")]
    [InlineData("15.0.8", "syn-3a7c0e9f1b2d4c6e")]
    [InlineData("15.0.6", "65a1b2c3d4e5f60718293a4b")]
    public async Task AnEditLandsOnTheCopyAV023EditLeftUnderTheRecordsFullUuid(string version, string legacyId)
    {
        var upstream = new FakeNightscoutTreatments(version);
        using (var client = new HttpClient(upstream))
        {
            (await client.PutAsJsonAsync("https://nightscout.example.com/api/v1/treatments", new JsonObject
            {
                ["_id"] = RecordUuid, ["identifier"] = RecordUuid, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At,
            })).IsSuccessStatusCode.Should().Be(version == "15.0.8");
        }

        await Sink(upstream).OnUpdatedAsync(Edited(legacyId, insulin: 2));
        await Sink(upstream).OnUpdatedAsync(Edited(legacyId, insulin: 3));

        Copies(upstream).Should().Equal((version == "15.0.8" ? RecordUuid : MongoObjectId.Coerce(legacyId), 3d));
    }

    /// <summary>
    /// A 15.0.8 that was a 15.0.6 when write-back POSTed the copy holds it under its 24-hex key as a
    /// string, which <c>find[_id]</c> casts past. The edit is POSTed under it, and 15.0.8 matches its
    /// identifier.
    /// </summary>
    [Fact]
    public async Task On1508_AnEditLandsOnACopyA1506PostLeftUnderItsHexKeyAsAString()
    {
        const string key = "65a1b2c3d4e5f60718293a4b";
        var upstream = new FakeNightscoutTreatments("15.0.8");
        upstream.Seed(new JsonObject { ["_id"] = key, ["identifier"] = key, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At }, asObjectId: false);

        await Sink(upstream).OnUpdatedAsync(Edited(key, insulin: 2));

        upstream.Documents.Select(d => (d.Id, d.IsObjectId, (string?)d.Document["identifier"], (double)d.Document["insulin"]!))
            .Should().Equal((key, false, key, 2d));
    }

    /// <summary>
    /// A copy found by its identifier under another string <c>_id</c>, such as a Loop override's
    /// uuid kept up to 15.0.6. An edit that moves it inserts under that taken <c>_id</c> and is
    /// refused, rather than stored beside it under a minted one; an edit that does not lands on it.
    /// </summary>
    [Fact]
    public async Task On1506_AnEditOfACopyUnderAnotherStringId_LandsOnItOrIsRefused()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var key = MongoObjectId.Coerce(legacyId)!;
        var upstream = new FakeNightscoutTreatments("15.0.6");
        upstream.Seed(new JsonObject { ["_id"] = "loop-override-7", ["identifier"] = key, ["eventType"] = "Correction Bolus", ["insulin"] = 1.0, ["created_at"] = At }, asObjectId: false);

        await Sink(upstream).OnUpdatedAsync(Edited(legacyId, insulin: 2, at: "2026-09-01T08:05:00.000Z"));
        upstream.Documents.Select(d => (d.Id, (double)d.Document["insulin"]!)).Should().Equal(("loop-override-7", 1d));
        upstream.Refusals.Should().Be(1);

        await Sink(upstream).OnUpdatedAsync(Edited(legacyId, insulin: 3));
        upstream.Documents.Select(d => (d.Id, (string?)d.Document["identifier"], (double)d.Document["insulin"]!))
            .Should().Equal(("loop-override-7", key, 3d));
    }

    /// <summary>The finds and the PUT the model answers as <c>query.js</c> and <c>save()</c> do at each version.</summary>
    [Theory]
    [InlineData("15.0.8")]
    [InlineData("15.0.6")]
    public async Task TheModelAnswersIdFindsAndAnIdLessPutAsTheVersionDoes(string version)
    {
        const string hex = "65a1b2c3d4e5f60718293a4b";
        var upstream = new FakeNightscoutTreatments(version);
        upstream.Seed(new JsonObject { ["_id"] = "xdrip-1", ["eventType"] = "Note", ["created_at"] = At }, asObjectId: false);
        upstream.Seed(new JsonObject { ["_id"] = hex, ["eventType"] = "Note", ["created_at"] = "2026-09-01T09:00:00.000Z" }, asObjectId: false);
        using var client = new HttpClient(upstream);
        const string url = "https://nightscout.example.com/api/v1/treatments.json";

        var byId = await client.GetAsync($"{url}?find[_id]=xdrip-1");
        if (version == "15.0.6")
            byId.StatusCode.Should().Be(System.Net.HttpStatusCode.InternalServerError);
        else
            (await byId.Content.ReadAsStringAsync()).Should().Contain("xdrip-1");
        (await client.GetStringAsync($"{url}?find[_id][$in][0]=xdrip-1")).Should().Contain("xdrip-1");
        (await client.GetStringAsync($"{url}?find[_id]={hex}")).Should().Be("[]");
        (await client.GetStringAsync($"{url}?find[_id][$in][0]={hex}")).Should().Be(version == "15.0.6" ? $$"""[{"_id":"{{hex}}","eventType":"Note","created_at":"2026-09-01T09:00:00.000Z"}]""" : "[]");

        (await client.PutAsJsonAsync(url.Replace(".json", ""), new JsonObject { ["eventType"] = "Note", ["created_at"] = "2026-09-01T10:00:00.000Z", ["notes"] = "id-less" }))
            .IsSuccessStatusCode.Should().BeTrue();
        upstream.Documents.Should().HaveCount(3);
        upstream.Documents[^1].IsObjectId.Should().BeTrue();
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
