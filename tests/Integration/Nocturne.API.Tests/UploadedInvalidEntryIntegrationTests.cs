using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Xunit;
using Xunit.Abstractions;
using LegacyEntry = Nocturne.Core.Models.Entry;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// An entry uploaded with <c>isValid: false</c> is stored deleted, as Nightscout stores it: no search
/// serves it, v3 history serves it as deleted, and an upload of its id without <c>isValid</c> brings it
/// back. Only the client's own id deletes a live reading: a reading at the same time is another one.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class UploadedInvalidEntryIntegrationTests : ApiIntegrationTestBase
{
    public UploadedInvalidEntryIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public static TheoryData<string, bool> Uploads()
    {
        var data = new TheoryData<string, bool>();
        foreach (var type in new[] { "sgv", "mbg", "cal" })
        {
            data.Add(type, false);
            data.Add(type, true);
        }

        return data;
    }

    private readonly DateTimeOffset _at = DateTimeOffset.UtcNow.AddMinutes(-10);

    private JsonObject Entry(string type, string? id = null, bool? isValid = null, double value = 120)
    {
        var entry = new JsonObject
        {
            ["type"] = type,
            ["date"] = _at.ToUnixTimeMilliseconds(),
            ["dateString"] = _at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["device"] = "AndroidAPS-DexcomG6",
        };
        if (id is not null)
            entry["_id"] = id;

        switch (type)
        {
            case "sgv":
                entry["sgv"] = value;
                entry["direction"] = "Flat";
                break;
            case "mbg":
                entry["mbg"] = value;
                break;
            case "cal":
                entry["slope"] = value;
                entry["intercept"] = 30000.0;
                entry["scale"] = 1.0;
                break;
        }

        if (isValid is { } valid)
            entry["isValid"] = valid;
        return entry;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];

    private static async Task UploadAsync(HttpClient client, JsonObject entry, bool v3)
    {
        var response = v3
            ? await client.PostAsJsonAsync("/api/v3/entries", entry)
            : await client.PostAsJsonAsync("/api/v1/entries", new JsonArray(entry.DeepClone()));
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>The ids v1 search, v3 search and v3 search by modification serve.</summary>
    private sealed record Live(string[] V1, string[] V3, string[] V3Modified)
    {
        public static Live None { get; } = new([], [], []);
    }

    private async Task<Live> LiveAsync(HttpClient client)
    {
        static string[] Ids(JsonArray documents, string key) =>
            [.. documents.Select(d => d![key]!.GetValue<string>()).Order()];

        await SettleHistoryAsync();
        return new Live(
            Ids((await GetAsync(client, "/api/v1/entries?count=100")).AsArray(), "_id"),
            Ids((await GetAsync(client, "/api/v3/entries?limit=100"))["result"]!.AsArray(), "identifier"),
            Ids((await GetAsync(client, "/api/v3/entries?limit=100&srvModified$gt=0&sort=srvModified"))["result"]!.AsArray(),
                "identifier"));
    }

    private static void ShouldServe(Live live, string id) =>
        live.Should().BeEquivalentTo(new Live([id], [id], [id]), o => o.WithStrictOrdering());

    private async Task<JsonArray> HistoryAsync(HttpClient client)
    {
        await SettleHistoryAsync();
        return (await GetAsync(client, "/api/v3/entries/history/0"))["result"]!.AsArray();
    }

    private static bool IsDeleted(JsonNode? entry) => entry?["isValid"]?.GetValue<bool>() == false;

    private static bool Is(JsonNode? entry, string id) => entry?["identifier"]?.GetValue<string>() == id;

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task An_upload_with_isValid_false_is_stored_deleted(string type, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();

        await UploadAsync(client, Entry(type, id, isValid: false), v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        var history = await HistoryAsync(client);
        history.Should().ContainSingle().Which.Should().Match<JsonNode>(e => IsDeleted(e) && Is(e, id));
        history[0]!["type"]!.GetValue<string>().Should().Be(type);
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_resend_without_isValid_brings_it_back(string type, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry(type, id, isValid: false), v3);

        await UploadAsync(client, Entry(type, id), v3);

        ShouldServe(await LiveAsync(client), id);
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_resend_with_isValid_false_stores_nothing_more(string type, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry(type, id, isValid: false), v3);

        await UploadAsync(client, Entry(type, id, isValid: false), v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().ContainSingle().Which.Should().Match<JsonNode>(e => IsDeleted(e));
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task An_upload_with_isValid_false_deletes_the_live_entry_its_id_names(string type, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry(type, id), v3);
        ShouldServe(await LiveAsync(client), id);

        await UploadAsync(client, Entry(type, id, isValid: false), v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().Contain(e => IsDeleted(e) && Is(e, id));

        await UploadAsync(client, Entry(type, id), v3);

        ShouldServe(await LiveAsync(client), id);
    }

    /// <summary>
    /// A reading at the same time, type and device is matched as the same reading when it is valid.
    /// An invalid upload that does not carry the live reading's id is another reading, so the live
    /// one stays served.
    /// </summary>
    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task An_upload_with_isValid_false_under_another_id_leaves_the_live_entry_at_its_time(string type, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry(type, id), v3);

        await UploadAsync(client, Entry(type, NewId(), isValid: false, value: 90), v3);
        await UploadAsync(client, Entry(type, isValid: false, value: 80), v3);

        ShouldServe(await LiveAsync(client), id);
        (await HistoryAsync(client)).Count(IsDeleted).Should().Be(2);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Two_versions_of_one_id_in_a_batch_leave_the_last(bool firstValid, bool lastValid)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();

        var response = await client.PostAsJsonAsync("/api/v1/entries", new JsonArray(
            Entry("sgv", id, isValid: firstValid ? null : false),
            Entry("sgv", id, isValid: lastValid ? null : false, value: 130)));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        if (lastValid)
            ShouldServe(await LiveAsync(client), id);
        else
            (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().ContainSingle()
            .Which.Should().Match<JsonNode>(e => IsDeleted(e) == !lastValid && Is(e, id));
    }

    /// <summary>
    /// A CGM connector reported the reading an AAPS upload did, and deduplication linked the two.
    /// AAPS invalidating its record takes only its own copy: the reading stays served from the other
    /// source.
    /// </summary>
    [Fact]
    public async Task An_upload_with_isValid_false_leaves_another_sources_copy_of_the_reading()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry("sgv", id), v3: false);
        await StoreAnotherSourcesCopyAsync(id);
        (await LiveAsync(client)).V1.Should().ContainSingle("deduplication serves one copy of the reading");

        await UploadAsync(client, Entry("sgv", id, isValid: false), v3: false);

        var live = await LiveAsync(client);
        (live.V1.Length, live.V3.Length).Should().Be((1, 1));
        live.V1.Should().NotContain(id);
    }

    [Theory]
    [InlineData("sgv")]
    [InlineData("mbg")]
    public async Task A_v3_update_that_sets_isValid_false_is_refused(string type)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry(type, id), v3: true);

        var put = await client.PutAsJsonAsync($"/api/v3/entries/{id}", Entry(type, id, isValid: false));
        var patch = await client.PatchAsJsonAsync($"/api/v3/entries/{id}", new JsonObject { ["isValid"] = false });

        foreach (var response in new[] { put, patch })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!.GetValue<string>()
                .Should().Be("Field isValid cannot be modified by the client");
        }

        ShouldServe(await LiveAsync(client), id);
    }

    /// <summary>
    /// An upload stored deleted reaches no one a live write reaches: no record change names it, so
    /// SignalR, the alert facts and the Home Assistant relay never see it, and no legacy entry is
    /// announced for it.
    /// </summary>
    [Theory]
    [InlineData("sgv")]
    [InlineData("mbg")]
    [InlineData("cal")]
    public async Task An_upload_stored_deleted_announces_no_record(string type)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        var writes = new RecordedWrites();

        await WithTenantScopeAsync(async sp =>
            await RecordingDecomposer(sp, writes).DecomposeBatchAsync([InvalidEntry(type, id)], WriteOrigin.Live));

        writes.Changes.Batches.Should().NotBeEmpty("the write still reports its record type to the caches");
        writes.Created.Should().BeEmpty();
        writes.Updated.Should().BeEmpty();
        writes.Deleted.Should().BeEmpty();
        writes.Entries.Created.Should().BeEmpty();
        writes.Entries.Deleted.Should().BeEmpty();
        writes.Readings.Relayed.Should().BeEmpty();
        (await HistoryAsync(client)).Should().ContainSingle().Which.Should().Match<JsonNode>(e => IsDeleted(e) && Is(e, id));
    }

    /// <summary>
    /// An upload that deletes the live reading its id names is announced as that reading's delete, to
    /// the record change and to the legacy entries, and as nothing else.
    /// </summary>
    [Theory]
    [InlineData("sgv")]
    [InlineData("mbg")]
    [InlineData("cal")]
    public async Task An_upload_that_deletes_a_live_reading_is_announced_as_its_delete(string type)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry(type, id), v3: false);
        var writes = new RecordedWrites();

        await WithTenantScopeAsync(async sp =>
            await RecordingDecomposer(sp, writes).DecomposeBatchAsync([InvalidEntry(type, id)], WriteOrigin.Live));

        writes.Deleted.Should().ContainSingle().Which.LegacyId.Should().Be(id);
        writes.Created.Should().BeEmpty();
        writes.Updated.Should().BeEmpty();
        writes.Entries.Deleted.Should().ContainSingle().Which!.Id.Should().Be(id);
        writes.Entries.Created.Should().BeEmpty();
        writes.Readings.Relayed.Should().BeEmpty();
        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
    }

    /// <summary>
    /// Deleting an AAPS reading another source's copy is linked to promotes that copy: the change names
    /// the delete of the one and the update of the other, and creates nothing.
    /// </summary>
    [Fact]
    public async Task An_upload_that_deletes_a_linked_reading_announces_the_promoted_copy_as_updated()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry("sgv", id), v3: false);
        await StoreAnotherSourcesCopyAsync(id);
        var writes = new RecordedWrites();

        await WithTenantScopeAsync(async sp =>
            await RecordingDecomposer(sp, writes).DecomposeBatchAsync([InvalidEntry("sgv", id)], WriteOrigin.Live));

        writes.Deleted.Should().ContainSingle().Which.LegacyId.Should().Be(id);
        writes.Updated.Should().ContainSingle().Which.LegacyId.Should().Be("dexcom_" + id);
        writes.Created.Should().BeEmpty();
    }

    /// <summary>
    /// A connector, the sample data feed or an import sends readings with no id. One sent with
    /// <c>isValid: false</c> names no reading, so it writes nothing, alone in its batch or not.
    /// </summary>
    [Theory]
    [InlineData("sgv")]
    [InlineData("mbg")]
    [InlineData("cal")]
    public async Task An_in_process_upload_with_isValid_false_and_no_id_writes_nothing(string type)
    {
        var client = CreateAuthenticatedClient();

        var written = await WithTenantScopeAsync(sp =>
            sp.GetRequiredService<IEntryService>().CreateEntriesAsync([InvalidEntry(type, id: null)], WriteOrigin.Live));

        written.Invalidated.Should().BeEmpty();
        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().BeEmpty();
    }

    /// <summary>
    /// A v3 read resolves an id across every entry type, the write only within the upload's own. An
    /// invalid upload whose id the read resolves but the write does not name deletes nothing, so it is
    /// answered as a create, not as a duplicate of the entry it left live.
    /// </summary>
    [Fact]
    public async Task A_v3_create_that_names_no_reading_it_deletes_is_answered_as_a_create()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry("mbg", id), v3: false);

        var response = await client.PostAsJsonAsync("/api/v3/entries", Entry("sgv", id, isValid: false));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["isDeduplication"]?.GetValue<bool>()
            .Should().NotBe(true);
        var live = await LiveAsync(client);
        (live.V1, live.V3).Should().BeEquivalentTo((new[] { id }, new[] { id }));
    }

    /// <summary>
    /// A deleted entry hides only the live entry of its own type: an mbg a later deleted sgv shares
    /// its id with is still served by a search by modification.
    /// </summary>
    [Fact]
    public async Task A_search_by_modification_serves_a_live_entry_whose_id_a_later_deleted_entry_of_another_type_shares()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry("mbg", id), v3: false);
        await UploadAsync(client, Entry("sgv", id, isValid: false), v3: false);

        var live = await LiveAsync(client);

        ShouldServe(live, id);
        (await HistoryAsync(client)).Select(e => (Type: e!["type"]!.GetValue<string>(), Deleted: IsDeleted(e)))
            .Should().BeEquivalentTo([("mbg", false), ("sgv", true)]);
    }

    [Fact]
    public async Task A_v3_create_that_deletes_the_reading_its_id_names_is_answered_as_its_duplicate()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Entry("sgv", id), v3: false);

        var response = await client.PostAsJsonAsync("/api/v3/entries", Entry("sgv", id, isValid: false));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["isDeduplication"]!.GetValue<bool>().Should().BeTrue();
        body["identifier"]!.GetValue<string>().Should().Be(id);
        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
    }

    private LegacyEntry InvalidEntry(string type, string? id) => new()
    {
        Id = id,
        Type = type,
        Mills = _at.ToUnixTimeMilliseconds(),
        Device = "AndroidAPS-DexcomG6",
        Sgv = type == "sgv" ? 120 : null,
        Mgdl = type == "sgv" ? 120 : 0,
        Direction = type == "sgv" ? "Flat" : null,
        Mbg = type == "mbg" ? 120 : null,
        Slope = type == "cal" ? 120 : null,
        Intercept = type == "cal" ? 30000 : null,
        Scale = type == "cal" ? 1 : null,
        IsValid = false,
    };

    private Task<V4Models.SensorGlucose> StoreAnotherSourcesCopyAsync(string id) =>
        WithTenantScopeAsync(sp => sp.GetRequiredService<ISensorGlucoseRepository>().CreateAsync(new V4Models.SensorGlucose
        {
            Timestamp = _at.UtcDateTime.AddSeconds(5),
            LegacyId = "dexcom_" + id,
            Device = "dexcom-connector",
            DataSource = "dexcom-connector",
            Mgdl = 120,
        }, WriteOrigin.Live));

    /// <summary>The entry decomposer over reading repositories whose broadcasts <paramref name="writes"/> records.</summary>
    private static EntryDecomposer RecordingDecomposer(IServiceProvider sp, RecordedWrites writes)
    {
        IRecordChangePublisher changes = writes.Changes;
        IDataEventSink<LegacyEntry> entries = writes.Entries;
        IGlucoseReadingBroadcaster readings = writes.Readings;
        return ActivatorUtilities.CreateInstance<EntryDecomposer>(
            sp,
            (ISensorGlucoseRepository)ActivatorUtilities.CreateInstance<SensorGlucoseRepository>(sp, changes, entries, readings),
            (IMeterGlucoseRepository)ActivatorUtilities.CreateInstance<MeterGlucoseRepository>(sp, changes, entries),
            (ICalibrationRepository)ActivatorUtilities.CreateInstance<CalibrationRepository>(sp, changes, entries));
    }
}
