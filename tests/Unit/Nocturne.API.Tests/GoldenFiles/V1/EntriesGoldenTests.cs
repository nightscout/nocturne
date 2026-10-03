using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class EntriesGoldenTests : GoldenFileTestBase
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Fixed timestamps for deterministic tests
    // 2024-03-26T12:00:00Z = 1711454400000
    private const long BaseMillis = 1711454400000;

    public EntriesGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region Helper Methods

    private static SensorGlucoseEntity CreateSgvEntry(
        int index,
        double sgv = 120,
        string direction = "Flat",
        string? legacyId = null)
    {
        var mills = BaseMillis - (index * 300_000); // 5 min apart, descending
        return new SensorGlucoseEntity
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{(index + 1):D12}"),
            TenantId = TestTenantId,
            LegacyId = legacyId ?? $"aaaaaaaaaaaaaaaaaaaaa{(index + 1):D3}",
            Mgdl = sgv,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
            Direction = direction,
            Device = "xDrip-DexcomG6",
            SysCreatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    private static SensorGlucoseEntity CreateFullSgvEntry()
    {
        return new SensorGlucoseEntity
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000099"),
            TenantId = TestTenantId,
            LegacyId = "aaaaaaaaaaaaaaaaaaaaa099",
            Mgdl = 145,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(BaseMillis).UtcDateTime,
            Direction = "FortyFiveUp",
            Device = "xDrip-DexcomG6",
            Noise = 1,
            Filtered = 162000,
            Unfiltered = 163000,
            Delta = 3.5,
            TrendRate = 1.5,
            UtcOffset = 0,
            SysCreatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    private static SensorGlucoseEntity CreateMinimalSgvEntry()
    {
        return new SensorGlucoseEntity
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000088"),
            TenantId = TestTenantId,
            LegacyId = "aaaaaaaaaaaaaaaaaaaaa088",
            Mgdl = 100,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(BaseMillis).UtcDateTime,
            Device = "xDrip-DexcomG6",
            SysCreatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            // All optional fields left null: Noise, Filtered, Unfiltered, Delta, Direction, TrendRate
        };
    }

    private static MeterGlucoseEntity CreateMbgEntry(int index)
    {
        var mills = BaseMillis - (index * 300_000);
        return new MeterGlucoseEntity
        {
            Id = Guid.Parse($"00000000-0000-0000-0001-{(index + 1):D12}"),
            TenantId = TestTenantId,
            LegacyId = $"bbbbbbbbbbbbbbbbbbbbb{(index + 1):D3}",
            Mgdl = 130,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
            Device = "Contour Next",
            SysCreatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            SysUpdatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    #endregion

    #region GET /api/v1/entries/current

    [Fact]
    public async Task Current_WithOneSgvEntry_ReturnsArrayWrappedSingleEntry()
    {
        await SeedSensorGlucose(CreateSgvEntry(0, sgv: 120, direction: "Flat"));

        var response = await Client.GetAsync("/api/v1/entries/current");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task Current_WithNoEntries_ReturnsEmptyArray()
    {
        // No seeding - empty database

        var response = await Client.GetAsync("/api/v1/entries/current");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task Current_WithAllOptionalFields_ReturnsAllFields()
    {
        await SeedSensorGlucose(CreateFullSgvEntry());

        var response = await Client.GetAsync("/api/v1/entries/current");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task Current_WithMinimalEntry_OmitsNullOptionalFields()
    {
        await SeedSensorGlucose(CreateMinimalSgvEntry());

        var response = await Client.GetAsync("/api/v1/entries/current");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region GET /api/v1/entries

    [Fact]
    public async Task GetEntries_With15Entries_ReturnsOnly10DefaultCount()
    {
        var entries = Enumerable.Range(0, 15).Select(i => CreateSgvEntry(i)).ToArray();
        await SeedSensorGlucose(entries);

        var response = await Client.GetAsync("/api/v1/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task GetEntries_WithCount3_RespectsLimit()
    {
        var entries = Enumerable.Range(0, 10).Select(i => CreateSgvEntry(i)).ToArray();
        await SeedSensorGlucose(entries);

        var response = await Client.GetAsync("/api/v1/entries?count=3");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task GetEntries_WithEmptyDb_ReturnsEmptyArray()
    {
        var response = await Client.GetAsync("/api/v1/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task GetEntries_SortedByDateDescending_NewestFirst()
    {
        // Seed entries with different timestamps; index 0 = newest, index 4 = oldest
        var entries = Enumerable.Range(0, 5).Select(i => CreateSgvEntry(i, sgv: 100 + i * 10)).ToArray();
        await SeedSensorGlucose(entries);

        var response = await Client.GetAsync("/api/v1/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task GetEntries_WithCacheBustingRrParam_StillReturnsNewestFirst()
    {
        // xDrip+ appends ?rr=<timestamp> purely to bypass cached server responses; legacy
        // Nightscout ignores it, so the sort order must remain newest-first.
        var entries = Enumerable.Range(0, 5).Select(i => CreateSgvEntry(i, sgv: 100 + i * 10)).ToArray();
        await SeedSensorGlucose(entries);

        var response = await Client.GetAsync("/api/v1/entries?rr=1711454400000");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region GET /api/v1/entries/sgv (type filter)

    [Fact]
    public async Task GetEntriesByType_Sgv_FiltersByType()
    {
        // Seed mix of SGV and MBG entries
        var sgvEntries = Enumerable.Range(0, 3).Select(i => CreateSgvEntry(i)).ToArray();
        var mbgEntries = Enumerable.Range(0, 2).Select(i => CreateMbgEntry(i + 10)).ToArray();
        await SeedSensorGlucose(sgvEntries);
        await SeedMeterGlucose(mbgEntries);

        var response = await Client.GetAsync("/api/v1/entries/sgv");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task GetEntriesByType_WithCount_ReturnsThatManyNewestFirst()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 300).Select(i => CreateSgvEntry(i)).ToArray());

        var entries = await GetEntryArrayAsync("/api/v1/entries/sgv.json?count=288");

        entries.Should().HaveCount(288);
        entries.Select(DateOf).Should().BeInDescendingOrder();
        DateOf(entries[0]).Should().Be(BaseMillis);
    }

    [Fact]
    public async Task GetEntriesByType_WithoutCount_ReturnsTenNewest()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 15).Select(i => CreateSgvEntry(i)).ToArray());

        var entries = await GetEntryArrayAsync("/api/v1/entries/sgv.json");

        entries.Should().HaveCount(10);
        DateOf(entries[0]).Should().Be(BaseMillis);
    }

    [Fact]
    public async Task GetEntriesByType_WithFindDateRange_NarrowsToTheRangeAndTheType()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 20).Select(i => CreateSgvEntry(i)).ToArray());
        await SeedMeterGlucose(Enumerable.Range(0, 3).Select(CreateMbgEntry).ToArray());
        var from = BaseMillis - 4 * 300_000;

        var entries = await GetEntryArrayAsync(
            $"/api/v1/entries/sgv.json?count=100&find[date][$gte]={from}");

        entries.Should().HaveCount(5);
        entries.Select(DateOf).Should().OnlyContain(date => date >= from);
        entries.Select(e => e.GetProperty("type").GetString()).Should().OnlyContain(t => t == "sgv");
    }

    [Fact]
    public async Task GetEntriesByType_PathTypeConstrainsACountedRead()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 20).Select(i => CreateSgvEntry(i)).ToArray());
        await SeedMeterGlucose(Enumerable.Range(0, 12).Select(CreateMbgEntry).ToArray());

        var entries = await GetEntryArrayAsync("/api/v1/entries/mbg.json?count=50");

        entries.Should().HaveCount(12);
        entries.Select(e => e.GetProperty("type").GetString()).Should().OnlyContain(t => t == "mbg");
    }

    [Fact]
    public async Task GetEntriesById_WithCount_StillReturnsTheOneEntry()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 5).Select(i => CreateSgvEntry(i)).ToArray());

        var entries = await GetEntryArrayAsync("/api/v1/entries/aaaaaaaaaaaaaaaaaaaaa003.json?count=50");

        entries.Should().ContainSingle();
        entries[0].GetProperty("_id").GetString().Should().Be("aaaaaaaaaaaaaaaaaaaaa003");
    }

    [Fact]
    public async Task GetEntriesByType_PathTypeReplacesFindTypeAlongsideAFieldFilter()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 6).Select(i => CreateSgvEntry(i, sgv: 100 + i * 10)).ToArray());
        await SeedMeterGlucose(Enumerable.Range(0, 3).Select(CreateMbgEntry).ToArray());

        var entries = await GetEntryArrayAsync(
            "/api/v1/entries/sgv.json?find[type]=mbg&find[sgv][$gte]=130");

        entries.Select(e => e.GetProperty("sgv").GetInt32()).Should().Equal(130, 140, 150);
        entries.Select(e => e.GetProperty("type").GetString()).Should().OnlyContain(t => t == "sgv");
    }

    [Fact]
    public async Task GetEntriesByType_PathTypeReplacesAnEncodedFindTypeOperator()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 4).Select(i => CreateSgvEntry(i)).ToArray());
        await SeedMeterGlucose(Enumerable.Range(0, 2).Select(CreateMbgEntry).ToArray());

        var entries = await GetEntryArrayAsync(
            "/api/v1/entries/sgv.json?find%5Btype%5D%5B%24ne%5D=sgv&find[sgv][$gte]=1");

        entries.Should().HaveCount(4);
        entries.Select(e => e.GetProperty("type").GetString()).Should().OnlyContain(t => t == "sgv");
    }

    [Fact]
    public async Task GetEntriesByType_WithCountZero_ReturnsEmptyArray()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 3).Select(i => CreateSgvEntry(i)).ToArray());

        var entries = await GetEntryArrayAsync("/api/v1/entries/sgv.json?count=0");

        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntriesByType_IfModifiedSinceAtTheNewestEntry_AnswersNotModified()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 3).Select(i => CreateSgvEntry(i)).ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/entries/sgv.json?count=2");
        request.Headers.IfModifiedSince = DateTimeOffset.FromUnixTimeMilliseconds(BaseMillis);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        response.Content.Headers.LastModified.Should()
            .Be(DateTimeOffset.FromUnixTimeMilliseconds(BaseMillis));
    }

    [Fact]
    public async Task GetEntriesByType_IfModifiedSinceBeforeTheNewestEntry_ReturnsTheEntries()
    {
        await SeedSensorGlucose(Enumerable.Range(0, 3).Select(i => CreateSgvEntry(i)).ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/entries/sgv.json?count=2");
        request.Headers.IfModifiedSince = DateTimeOffset.FromUnixTimeMilliseconds(BaseMillis - 60_000);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.LastModified.Should()
            .Be(DateTimeOffset.FromUnixTimeMilliseconds(BaseMillis));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GetEntriesByType_EmptyResult_SendsNoLastModifiedAndNeverNotModified()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/entries/sgv.json");
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddYears(1);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.LastModified.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().Be("[]");
    }

    private async Task<JsonElement[]> GetEntryArrayAsync(string url)
    {
        var response = await Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static long DateOf(JsonElement entry) => entry.GetProperty("date").GetInt64();

    #endregion

    #region POST /api/v1/entries

    [Fact]
    public async Task PostEntries_SingleEntry_ReturnsCreatedResponseShape()
    {
        var payload = new
        {
            type = "sgv",
            sgv = 180,
            dateString = "2024-03-26T12:00:00.000Z",
            date = 1711454400000L,
            device = "xDrip-DexcomG6",
            direction = "SingleUp",
        };

        var response = await PostJsonAsync("/api/v1/entries", payload);
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("_id", "mills", "date", "sysTime", "created_at");
    }

    [Fact]
    public async Task PostEntries_Batch_ReturnsCreatedResponseShape()
    {
        var payload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 150,
                dateString = "2024-03-26T12:00:00.000Z",
                date = 1711454400000L,
                device = "xDrip-DexcomG6",
                direction = "Flat",
            },
            new
            {
                type = "sgv",
                sgv = 155,
                dateString = "2024-03-26T12:05:00.000Z",
                date = 1711454700000L,
                device = "xDrip-DexcomG6",
                direction = "FortyFiveUp",
            },
        };

        var response = await PostJsonAsync("/api/v1/entries", payload);
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("_id", "mills", "date", "sysTime", "created_at");
    }

    // NightscoutKit fails any upload that does not answer exactly 200 and requeues the batch.
    [Fact]
    public async Task PostEntries_EmptyArray_AnswersOkWithEmptyArray()
    {
        var response = await PostJsonAsync("/api/v1/entries", Array.Empty<object>());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PostEntries_EveryEntryRefused_AnswersOkWithOneEchoPerEntry()
    {
        var response = await PostJsonAsync("/api/v1/entries", new[] { new { type = "sgv" }, new { type = "sgv" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task PostEntries_UnparseableBody_AnswersBadRequest()
    {
        var content = new StringContent("{not json", Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/api/v1/entries", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion
}
