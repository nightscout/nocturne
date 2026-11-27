using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Entries;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Extensions;
using Xunit;
using static Nocturne.API.Tests.TestDoubles.StorageBroadcastCapture;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// The <c>entries</c> storage events as NS v3 socket clients read them: a delete carries the top-level
/// <c>colName</c> and <c>identifier</c> AAPS's <c>onDataDelete</c> routes and looks up by, and every id a
/// create carries is the id REST serves for the same entry.
/// </summary>
[Trait("Category", "Unit")]
public class EntryStorageBroadcastContractTests
{
    private const string RecordGuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";

    private static SignalREntryEventSink CreateEntrySink(StorageBroadcastCapture capture) =>
        new(Mock.Of<ITenantCache>(), capture.Broadcast, NullLogger<SignalREntryEventSink>.Instance);

    /// <summary>
    /// An <see cref="Entry"/> carries an <c>identifier</c> of its own, coerced to the ObjectId form.
    /// </summary>
    [Fact]
    public async Task EntrySink_SingleDelete_CarriesColNameAndIdentifier()
    {
        var capture = new StorageBroadcastCapture();

        await CreateEntrySink(capture).OnDeletedAsync(new Entry { Id = RecordGuid, Sgv = 120 });

        capture.Delete.GetProperty("colName").GetString().Should().Be("entries");
        Identifier(capture.Delete).Should().Be(MongoObjectId.Coerce(RecordGuid));
        capture.Delete.GetProperty("doc").GetProperty("_id").GetString().Should().Be(MongoObjectId.Coerce(RecordGuid));
    }

    [Fact]
    public async Task EntrySink_BulkDelete_CarriesColNameAndCountButNoIdentifier()
    {
        var capture = new StorageBroadcastCapture();

        await CreateEntrySink(capture).OnBulkDeletedAsync(17);

        capture.Delete.GetProperty("deletedCount").GetInt64().Should().Be(17);
        capture
            .Delete.TryGetProperty("identifier", out _)
            .Should()
            .BeFalse("a range delete materialises no ids; clients reconcile it on catch-up");
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
        var capture = new StorageBroadcastCapture();
        var sink = CreateEntrySink(capture);

        await sink.OnCreatedAsync([entry]);
        await sink.OnDeletedAsync(entry);

        var doc = capture.Create.GetProperty("doc");
        doc.GetProperty("_id")
            .GetString()
            .Should()
            .Be(doc.GetProperty("identifier").GetString(), "one record cannot ship two ids");
        Identifier(capture.Delete).Should().Be(doc.GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task DeleteIdentifier_MatchesTheV3RestProjection_ForEntries()
    {
        var entry = new Entry { Id = RecordGuid, Sgv = 120 };

        var capture = new StorageBroadcastCapture();
        await CreateEntrySink(capture).OnDeletedAsync(entry);

        Identifier(capture.Delete).Should().Be(RestIdentifier(new EntryV3Response(entry)));
    }

    /// <summary>
    /// A client that ingests a record over the socket and later reconciles it over REST dedupes on
    /// the id; AAPS stores whichever <c>identifier</c> delivered the record and looks deletes up by
    /// it. Every id-bearing key the create event carries must therefore be the id REST serves for the
    /// same record — a record id kept in a non-uuid <c>LegacyId</c> included, which coerces by hash.
    /// </summary>
    [Theory]
    [InlineData(RecordGuid)]
    [InlineData("dexcom_1722945600000")]
    public async Task CreateIds_MatchTheRestProjections_ForEntries(string recordId)
    {
        var entry = new Entry { Id = recordId, Sgv = 120 };
        var capture = new StorageBroadcastCapture();

        await CreateEntrySink(capture).OnCreatedAsync([entry]);

        var v1 = Rest(new EntryV1Response(entry)).GetProperty("_id").GetString();
        var v3 = RestIdentifier(new EntryV3Response(entry));
        v1.Should().Be(v3);
        IdKeys(capture.Create.GetProperty("doc"))
            .Should()
            .BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = v3, ["identifier"] = v3 });
    }

    /// <summary>
    /// The bulk keys are the same wire contract as the single-record ones, so a reconfigured
    /// payload serializer must not be able to rename them either.
    /// </summary>
    [Fact]
    public async Task BulkDeleteKeys_SurviveAReconfiguredPayloadSerializer()
    {
        var capture = new StorageBroadcastCapture(o => o.PropertyNamingPolicy = null);

        await CreateEntrySink(capture).OnBulkDeletedAsync(17);

        capture.Delete.GetProperty("colName").GetString().Should().Be("entries");
        capture.Delete.GetProperty("deletedCount").GetInt64().Should().Be(17);
    }
}
