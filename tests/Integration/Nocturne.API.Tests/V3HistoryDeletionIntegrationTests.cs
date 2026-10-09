using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Nightscout's v3 DELETE keeps the document with <c>isValid: false</c> and a new <c>srvModified</c>,
/// and <c>history</c> returns it (<c>lib/api3/generic/delete</c>, <c>lib/api3/generic/history</c>);
/// a client that syncs through history learns of a delete no other way. Each test creates through v3,
/// syncs, deletes, and reads the history from the cursor the sync left the client at.
/// </summary>
[Trait("Category", "Integration")]
public partial class V3HistoryDeletionIntegrationTests : ApiIntegrationTestBase
{
    private static readonly long Date =
        DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds() / 1000 * 1000;

    private static readonly string Iso =
        DateTimeOffset.FromUnixTimeMilliseconds(Date).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public V3HistoryDeletionIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task DeletedEntry_IsServedWithIsValidFalse()
    {
        await CreateAsync("entries", new
        {
            type = "sgv", sgv = 187, date = Date, device = "it-v3", app = "it", utcOffset = 0,
        });

        var tombstone = await DeleteAfterSyncAsync("entries", doc => Number(doc, "sgv") == 187);

        tombstone.GetProperty("sgv").GetInt32().Should().Be(187);
    }

    [Fact]
    public async Task DeletedCanonicalEntry_SendsTheBucketsOtherStreamAgain()
    {
        // Far enough apart in value that deduplication does not link them: the other stream's
        // reading is its own record, so deleting the canonical one leaves it standing.
        // Both inside one canonical bucket: buckets are fixed 5-minute windows, so two readings 30 s
        // apart at an arbitrary time straddle a boundary one run in ten and both win their own bucket.
        var bucket = Date / 300_000 * 300_000;
        await CreateAsync("entries", new { type = "sgv", sgv = 150, date = bucket + 60_000, device = "it-cgm-a", app = "it", utcOffset = 0 });
        await CreateAsync("entries", new { type = "sgv", sgv = 158, date = bucket + 90_000, device = "it-cgm-b", app = "it", utcOffset = 0 });

        var (synced, cursor) = await HistoryAsync("entries", 0);
        var winner = synced.Should().ContainSingle(doc => Number(doc, "sgv") == 150 || Number(doc, "sgv") == 158,
            "overlapping streams deliver one canonical reading per bucket").Subject;
        var otherSgv = Number(winner, "sgv") == 150 ? 158 : 150;

        (await AuthenticatedClient.DeleteAsync($"/api/v3/entries/{IdOf(winner)}")).IsSuccessStatusCode.Should().BeTrue();

        var (next, nextCursor) = await HistoryAsync("entries", cursor);
        next.Should().ContainSingle(doc => IdOf(doc) == IdOf(winner))
            .Which.GetProperty("isValid").GetBoolean().Should().BeFalse();
        var successor = next.Should().ContainSingle(doc => Number(doc, "sgv") == otherSgv).Subject;
        successor.GetProperty("isValid").GetBoolean().Should().BeTrue();
        successor.GetProperty("srvModified").GetInt64().Should().BeGreaterThan(cursor).And.BeLessThanOrEqualTo(nextCursor);
    }

    [Fact]
    public async Task DeletingOneCopyOfADuplicatedReading_TombstonesEveryCopy()
    {
        // A user deleting a reading deletes it whichever source reported it; only a whole-source
        // delete hands the group to the other source's copy.
        await CreateAsync("entries", new { type = "sgv", sgv = 230, date = Date - 600_000, device = "it-dup-a", app = "it", utcOffset = 0 });
        await CreateAsync("entries", new { type = "sgv", sgv = 230, date = Date - 590_000, device = "it-dup-b", app = "it", utcOffset = 0 });

        var (synced, cursor) = await HistoryAsync("entries", 0);
        var winner = synced.Should().ContainSingle(doc => Number(doc, "sgv") == 230).Subject;

        (await AuthenticatedClient.DeleteAsync($"/api/v3/entries/{IdOf(winner)}")).IsSuccessStatusCode.Should().BeTrue();

        var (next, _) = await HistoryAsync("entries", cursor);
        var copies = next.Where(doc => Number(doc, "sgv") == 230).ToList();
        copies.Select(doc => doc.GetProperty("device").GetString())
            .Should().BeEquivalentTo(["it-dup-a", "it-dup-b"]);
        copies.Should().OnlyContain(doc => !doc.GetProperty("isValid").GetBoolean());
        (await SensorDevicesAsync("it-dup-a", "it-dup-b")).Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingOneCopyOfADuplicatedBolusInV4_DeletesEveryCopy()
    {
        foreach (var (device, offset) in new[] { ("it-pump", 0), ("it-aaps", 10_000) })
        {
            await CreateAsync("treatments", new
            {
                eventType = "Correction Bolus", insulin = 2.7, date = Date - 1_200_000 + offset, app = "it", device, utcOffset = 0,
                data_source = device,
            });
        }

        var (_, cursor) = await HistoryAsync("treatments", 0);
        var boluses = await BolusIdsAsync(2.7);
        boluses.Should().ContainSingle("deduplication shows the pair through its primary");

        (await AuthenticatedClient.DeleteAsync($"/api/v4/insulin/boluses/{boluses[0]}")).IsSuccessStatusCode.Should().BeTrue();

        (await BolusIdsAsync(2.7)).Should().BeEmpty("no copy may keep counting in IOB");
        var (next, _) = await HistoryAsync("treatments", cursor);
        next.Where(doc => Number(doc, "insulin") == 2.7).Should().HaveCount(2)
            .And.OnlyContain(doc => !doc.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task RestoringTheNonPrimaryCopyOfADeletedDuplicateBolus_RestoresItsGroupWithOneLivePrimary()
    {
        foreach (var (device, offset) in new[] { ("it-pump", 0), ("it-aaps", 10_000) })
        {
            await CreateAsync("treatments", new
            {
                eventType = "Correction Bolus", insulin = 3.1, date = Date - 1_500_000 + offset, app = "it", device, utcOffset = 0,
                data_source = device,
            });
        }

        var primary = (await BolusIdsAsync(3.1)).Should().ContainSingle().Subject;
        (await AuthenticatedClient.DeleteAsync($"/api/v4/insulin/boluses/{primary}")).IsSuccessStatusCode.Should().BeTrue();
        var (_, synced) = await HistoryAsync("treatments", 0);

        var deleted = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/insulin/boluses/deleted?limit=100");
        var copy = deleted.GetProperty("data").EnumerateArray()
            .Where(b => b.GetProperty("insulin").GetDouble() == 3.1)
            .Select(b => b.GetProperty("id").GetString())
            .Should().HaveCount(2).And.Contain(primary).And.Subject.Single(id => id != primary);

        var restore = await AuthenticatedClient.PostAsync($"/api/v4/insulin/boluses/{copy}/restore", null);
        restore.IsSuccessStatusCode.Should().BeTrue(await restore.Content.ReadAsStringAsync());

        (await BolusIdsAsync(3.1)).Should().Equal([primary], "the group reads through its one primary again, so the dose counts");
        var (next, _) = await HistoryAsync("treatments", synced);
        var resent = next.Where(doc => Number(doc, "insulin") == 3.1).Should()
            .ContainSingle("history sends the group's one primary").Subject;
        IsTombstone(resent).Should().BeFalse("the primary is re-sent live");
    }

    [Fact]
    public async Task ABolusTwoSourcesUploaded_IsSentOnceByHistory()
    {
        foreach (var (device, offset) in new[] { ("it-pump", 0), ("it-aaps", 10_000) })
        {
            await CreateAsync("treatments", new
            {
                eventType = "Correction Bolus", insulin = 4.2, date = Date - 1_800_000 + offset, app = "it", device, utcOffset = 0,
                data_source = device,
            });
        }

        var (docs, _) = await HistoryAsync("treatments", 0);
        var sent = docs.Where(doc => Number(doc, "insulin") == 4.2).Should()
            .ContainSingle("the dose would count twice in a client syncing history").Subject;
        IsTombstone(sent).Should().BeFalse();
    }

    private async Task<List<string?>> BolusIdsAsync(double insulin)
    {
        var page = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/insulin/boluses?limit=100");
        return page.GetProperty("data").EnumerateArray()
            .Where(b => b.GetProperty("insulin").GetDouble() == insulin)
            .Select(b => b.GetProperty("id").GetString())
            .ToList();
    }

    [Fact]
    public async Task DeletingTheWinningSource_TombstonesItsReadings_AndSendsTheOtherSourcesCopiesLive()
    {
        // Two CGM sources report the same two readings; deduplication links each pair under the
        // first source's copy. Deleting that source must hand every pair to the other source.
        foreach (var (device, offset) in new[] { ("it-src-a", 0), ("it-src-b", 10_000) })
        {
            await CreateAsync("entries", new { type = "sgv", sgv = 170, date = Date + offset, device, app = "it", utcOffset = 0 });
            await CreateAsync("entries", new { type = "sgv", sgv = 210, date = Date + 300_000 + offset, device, app = "it", utcOffset = 0 });
        }

        (await SensorDevicesAsync()).Should().BeEquivalentTo(["it-src-a", "it-src-a"], "the first source leads each pair");
        var (_, cursor) = await HistoryAsync("entries", 0);

        var deleted = await AuthenticatedClient.DeleteAsync("/api/v4/services/data-sources/it-src-a");
        deleted.IsSuccessStatusCode.Should().BeTrue(await deleted.Content.ReadAsStringAsync());

        var (next, nextCursor) = await HistoryAsync("entries", cursor);
        var sent = next.Select(doc => (Device: doc.GetProperty("device").GetString(), Sgv: Number(doc, "sgv"),
            Valid: doc.GetProperty("isValid").GetBoolean())).ToList();
        sent.Should().Contain([("it-src-a", 170, false), ("it-src-a", 210, false), ("it-src-b", 170, true), ("it-src-b", 210, true)]);
        next.Where(doc => doc.GetProperty("device").GetString() == "it-src-b")
            .Should().OnlyContain(doc => doc.GetProperty("srvModified").GetInt64() > cursor
                && doc.GetProperty("srvModified").GetInt64() <= nextCursor);

        (await SensorDevicesAsync()).Should().BeEquivalentTo(["it-src-b", "it-src-b"]);
    }

    /// <summary>The devices among <paramref name="devices"/> of the sensor readings a normal v4 read shows.</summary>
    private async Task<List<string?>> SensorDevicesAsync(params string[] devices)
    {
        if (devices.Length == 0)
            devices = ["it-src-a", "it-src-b"];
        var page = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/glucose/sensor?limit=100");
        return page.GetProperty("data").EnumerateArray()
            .Select(g => g.GetProperty("device").GetString())
            .Where(d => devices.Contains(d))
            .ToList();
    }

    [Fact]
    public async Task DeletedTreatment_IsServedWithIsValidFalse()
    {
        await CreateAsync("treatments", new
        {
            eventType = "Correction Bolus", insulin = 1.35, date = Date, app = "it", device = "it-v3", utcOffset = 0,
        });

        var tombstone = await DeleteAfterSyncAsync("treatments", doc => Number(doc, "insulin") == 1.35);

        tombstone.GetProperty("eventType").GetString().Should().Be("Correction Bolus");
    }

    [Fact]
    public async Task DeletedDeviceStatus_IsServedWithIsValidFalse()
    {
        await CreateAsync("devicestatus", new
        {
            date = Date, created_at = Iso, device = "openaps://it-v3", app = "it", utcOffset = 0,
            openaps = new
            {
                iob = new { iob = 0.4, time = Iso },
                suggested = new { bg = 125, eventualBG = 118, reason = "it", timestamp = Iso },
            },
        });

        var tombstone = await DeleteAfterSyncAsync(
            "devicestatus", doc => doc.TryGetProperty("device", out var device) && device.GetString() == "openaps://it-v3");

        tombstone.GetProperty("device").GetString().Should().Be("openaps://it-v3");
    }

    [Fact]
    public async Task DeletedFood_IsServedWithIsValidFalse()
    {
        await CreateAsync("food", new
        {
            type = "food", name = "it-v3 apple", carbs = 14, portion = 100, unit = "g", date = Date, created_at = Iso,
        });

        var tombstone = await DeleteAfterSyncAsync(
            "food", doc => doc.TryGetProperty("name", out var name) && name.GetString() == "it-v3 apple");

        tombstone.GetProperty("name").GetString().Should().Be("it-v3 apple");
    }

    [Fact]
    public async Task DeletedProfile_IsServedWithIsValidFalse()
    {
        ScheduleEntry[] schedule = [new("00:00", 1.0, 0)];
        await CreateAsync("profile", new
        {
            defaultProfile = "Default",
            startDate = Iso,
            created_at = Iso,
            mills = Date,
            units = "mg/dl",
            store = new Dictionary<string, object>
            {
                ["Default"] = new
                {
                    dia = 4, units = "mg/dl", timezone = "UTC",
                    basal = schedule, carbratio = schedule, sens = schedule,
                    target_low = schedule, target_high = schedule,
                },
            },
        });

        var tombstone = await DeleteAfterSyncAsync("profile", doc => doc.GetProperty("defaultProfile").GetString() == "Default");

        tombstone.GetProperty("defaultProfile").GetString().Should().Be("Default");
    }

    [Fact]
    public async Task OneStoreOfATwoStoreProfileDeleted_ResendsTheDocumentLive_UntilItsLastStoreGoes()
    {
        ScheduleEntry[] schedule = [new("00:00", 1.0, 0)];
        object Store() => new
        {
            dia = 4, units = "mg/dl", timezone = "UTC",
            basal = schedule, carbratio = schedule, sens = schedule,
            target_low = schedule, target_high = schedule,
        };
        await CreateAsync("profile", new
        {
            defaultProfile = "it-day",
            startDate = Iso,
            created_at = Iso,
            mills = Date,
            units = "mg/dl",
            store = new Dictionary<string, object> { ["it-day"] = Store(), ["it-night"] = Store() },
        });

        var (synced, cursor) = await HistoryAsync("profile", 0);
        var documentId = IdOf(synced.Should().Contain(doc => HasStore(doc, "it-day")).Which);

        (await AuthenticatedClient.DeleteAsync($"/api/v4/profile/settings/{await SettingsIdAsync("it-day")}"))
            .IsSuccessStatusCode.Should().BeTrue();

        var (next, nextCursor) = await HistoryAsync("profile", cursor);
        var resent = next.Should().ContainSingle(doc => IdOf(doc) == documentId).Subject;
        resent.TryGetProperty("isValid", out _).Should().BeFalse("another store of the document is live");
        HasStore(resent, "it-night").Should().BeTrue();
        HasStore(resent, "it-day").Should().BeFalse();
        resent.GetProperty("srvModified").GetInt64().Should().BeGreaterThan(cursor).And.BeLessThanOrEqualTo(nextCursor);

        (await AuthenticatedClient.DeleteAsync($"/api/v4/profile/settings/{await SettingsIdAsync("it-night")}"))
            .IsSuccessStatusCode.Should().BeTrue();

        var (last, _) = await HistoryAsync("profile", nextCursor);
        last.Should().ContainSingle(doc => IdOf(doc) == documentId)
            .Which.GetProperty("isValid").GetBoolean().Should().BeFalse();
    }

    private static bool HasStore(JsonElement doc, string name) =>
        doc.TryGetProperty("store", out var store) && store.TryGetProperty(name, out _);

    private async Task<Guid> SettingsIdAsync(string profileName)
    {
        var rows = await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v4/profile/settings/by-name/{profileName}");
        return rows.EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task MealWhoseCarbsAreDeleted_IsResentUnderItsIdWithoutThem()
    {
        var (meal, cursor, _, carbId) = await SyncMealAsync(insulin: 3.3, carbs: 33);

        (await AuthenticatedClient.DeleteAsync($"/api/v4/nutrition/carbs/{carbId}")).IsSuccessStatusCode.Should().BeTrue();

        var (next, _) = await HistoryAsync("treatments", cursor);
        var survivor = next.Should().ContainSingle(doc => IdOf(doc) == meal).Subject;
        survivor.TryGetProperty("isValid", out _).Should().BeFalse();
        survivor.GetProperty("eventType").GetString().Should().Be("Correction Bolus");
        Number(survivor, "carbs").Should().BeNull();
        Number(survivor, "insulin").Should().Be(3.3);
    }

    [Fact]
    public async Task MealWhoseBolusIsDeleted_IsTombstoned_AndItsCarbsAreResentUnderTheirOwnId()
    {
        var (meal, cursor, bolusId, _) = await SyncMealAsync(insulin: 4.4, carbs: 44);

        (await AuthenticatedClient.DeleteAsync($"/api/v4/insulin/boluses/{bolusId}")).IsSuccessStatusCode.Should().BeTrue();

        var (next, _) = await HistoryAsync("treatments", cursor);
        next.Should().ContainSingle(doc => IdOf(doc) == meal)
            .Which.GetProperty("isValid").GetBoolean().Should().BeFalse();
        var carbs = next.Should().ContainSingle(doc => Number(doc, "carbs") == 44).Subject;
        IdOf(carbs).Should().NotBe(meal);
        carbs.GetProperty("eventType").GetString().Should().Be("Carb Correction");
        carbs.TryGetProperty("isValid", out _).Should().BeFalse();
    }

    /// <summary>
    /// Creates a meal through v3, syncs the treatment history, and returns the meal's history id,
    /// the cursor the sync ended on, and the V4 ids of its bolus and carb intake.
    /// </summary>
    private async Task<(string Meal, long Cursor, Guid BolusId, Guid CarbId)> SyncMealAsync(double insulin, double carbs)
    {
        await CreateAsync("treatments", new
        {
            eventType = "Meal Bolus", insulin, carbs, date = Date, app = "it", device = "it-v3", utcOffset = 0,
        });

        var (synced, cursor) = await HistoryAsync("treatments", 0);
        var meal = synced.Should().ContainSingle(doc => Number(doc, "insulin") == insulin).Subject;
        Number(meal, "carbs").Should().Be(carbs);

        var boluses = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/insulin/boluses?limit=100");
        var intakes = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/nutrition/carbs?limit=100");
        var bolusId = boluses.GetProperty("data").EnumerateArray()
            .Single(b => b.GetProperty("insulin").GetDouble() == insulin).GetProperty("id").GetGuid();
        var carbId = intakes.GetProperty("data").EnumerateArray()
            .Single(c => c.GetProperty("carbs").GetDouble() == carbs).GetProperty("id").GetGuid();

        return (IdOf(meal)!, cursor, bolusId, carbId);
    }

    [Fact]
    public async Task Delete_MovesTheCollectionLastModified_ToTheTombstone()
    {
        await CreateAsync("treatments", new
        {
            eventType = "Correction Bolus", insulin = 2.15, date = Date, app = "it", device = "it-v3", utcOffset = 0,
        });

        var tombstone = await DeleteAfterSyncAsync("treatments", doc => Number(doc, "insulin") == 2.15);

        var lastModified = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v3/lastModified");
        lastModified.GetProperty("result").GetProperty("collections").GetProperty("treatments").GetInt64()
            .Should().BeGreaterThanOrEqualTo(tombstone.GetProperty("srvModified").GetInt64());
    }

    /// <summary>
    /// Syncs <paramref name="collection"/> from the start, deletes the document
    /// <paramref name="match"/> finds by the identifier history served, and returns that document as
    /// the history read from the sync's cursor serves it.
    /// </summary>
    private async Task<JsonElement> DeleteAfterSyncAsync(string collection, Func<JsonElement, bool> match)
    {
        var (synced, cursor) = await HistoryAsync(collection, 0);
        var live = synced.Should().ContainSingle(doc => match(doc)).Subject;
        (live.TryGetProperty("isValid", out var valid) && valid.ValueKind == JsonValueKind.False)
            .Should().BeFalse("the document is live");

        var deleted = await AuthenticatedClient.DeleteAsync($"/api/v3/{collection}/{IdOf(live)}");
        deleted.IsSuccessStatusCode.Should().BeTrue();

        var (next, nextCursor) = await HistoryAsync(collection, cursor);
        var tombstone = next.Should().ContainSingle(doc => IdOf(doc) == IdOf(live)).Subject;
        tombstone.GetProperty("isValid").GetBoolean().Should().BeFalse();
        tombstone.GetProperty("srvModified").GetInt64().Should().BeGreaterThan(cursor);
        nextCursor.Should().BeGreaterThanOrEqualTo(tombstone.GetProperty("srvModified").GetInt64());

        var (drained, _) = await HistoryAsync(collection, nextCursor);
        drained.Should().NotContain(doc => IdOf(doc) == IdOf(live));
        return tombstone;
    }

    private async Task<(List<JsonElement> Docs, long Cursor)> HistoryAsync(string collection, long since)
    {
        var response = await AuthenticatedClient.GetAsync($"/api/v3/{collection}/history/{since}?limit=100");
        response.IsSuccessStatusCode.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var docs = body.GetProperty("result").EnumerateArray().ToList();
        if (response.Headers.ETag is not { } etag)
        {
            docs.Should().BeEmpty("a page with documents carries its cursor");
            return (docs, since);
        }

        return (docs, long.Parse(CursorPattern().Match(etag.ToString()).Groups[1].Value));
    }

    private async Task CreateAsync(string collection, object document)
    {
        var response = await AuthenticatedClient.PostAsJsonAsync($"/api/v3/{collection}", document);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static string? IdOf(JsonElement doc) =>
        doc.TryGetProperty("identifier", out var identifier) ? identifier.GetString()
        : doc.TryGetProperty("_id", out var id) ? id.GetString()
        : null;

    private static bool IsTombstone(JsonElement doc) =>
        doc.TryGetProperty("isValid", out var valid) && valid.ValueKind == JsonValueKind.False;

    private static double? Number(JsonElement doc, string property) =>
        doc.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    [GeneratedRegex("\"(\\d+)\"")]
    private static partial Regex CursorPattern();

    private sealed record ScheduleEntry(string time, double value, int timeAsSeconds);
}
