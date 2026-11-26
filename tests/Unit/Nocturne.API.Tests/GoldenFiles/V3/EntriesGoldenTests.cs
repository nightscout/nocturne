using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.GoldenFiles.V3;

public class EntriesGoldenTests : GoldenFileTestBase
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Use a timestamp close to "now" to avoid being filtered by the default date range.
    private static readonly long BaseMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
    }

    #endregion

    #region GET /api/v3/entries/history (canonical successor)

    [Fact]
    public async Task GetHistory_CanonicalSuccessorOfADeletedReading_ServesItsKeptFieldsAsTheByIdReadDoes()
    {
        var bucketStart = DateTimeOffset.FromUnixTimeMilliseconds(1768563300000L).UtcDateTime;
        var successorId = Guid.Parse("5f0a1b2c-3d4e-5f60-8172-93a4b5c6d7e8");
        var deletedId = Guid.Parse("6a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");
        var successorStamped = bucketStart.AddMinutes(10);
        var deletedStamped = successorStamped.AddMinutes(1);
        await SeedSensorGlucose(
            new SensorGlucoseEntity
            {
                Id = deletedId,
                TenantId = GoldenTenantId,
                Mgdl = 100,
                Timestamp = bucketStart,
                Device = "xDrip-DexcomG6",
                SysCreatedAt = bucketStart,
                SysUpdatedAt = deletedStamped,
                DeletedAt = deletedStamped,
            },
            new SensorGlucoseEntity
            {
                Id = successorId,
                TenantId = GoldenTenantId,
                Mgdl = 110,
                Timestamp = bucketStart.AddMinutes(1),
                Device = "xDrip-DexcomG6",
                AdditionalPropertiesJson = """{"units":"mg/dl","isReadOnly":false}""",
                SysCreatedAt = bucketStart,
                SysUpdatedAt = successorStamped,
            });
        await StampModified((deletedId, deletedStamped), (successorId, successorStamped));

        var cursor = new DateTimeOffset(successorStamped.AddSeconds(30)).ToUnixTimeMilliseconds();
        var history = await Client.GetAsync($"/api/v3/entries/history/{cursor}");

        var resent = JsonNode.Parse(await history.Content.ReadAsStringAsync())!["result"]!.AsArray()
            .Single(e => e!["sgv"]!.GetValue<int>() == 110)!;
        var byId = await Client.GetAsync($"/api/v3/entries/{resent["identifier"]!.GetValue<string>()}");
        var expected = JsonNode.Parse(await byId.Content.ReadAsStringAsync())!["result"]!;
        expected["units"]!.GetValue<string>().Should().Be("mg/dl");

        resent.AsObject().Remove("srvModified");
        expected.AsObject().Remove("srvModified");
        JsonNode.DeepEquals(resent, expected).Should().BeTrue();
    }

    // A save stamps sys_updated_at itself, so the history cursor needs the stamp set afterwards.
    private async Task StampModified(params (Guid Id, DateTime At)[] stamps)
    {
        using var scope = Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(GoldenTenantId);
        foreach (var (id, at) in stamps)
        {
            await db.SensorGlucose.IgnoreQueryFilters().Where(e => e.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SysUpdatedAt, at));
        }
    }

    #endregion

    #region GET /api/v3/entries (empty)

    [Fact]
    public async Task GetEntries_WithEmptyDb_ReturnsV3WrappedEmptyResult()
    {
        var response = await Client.GetAsync("/api/v3/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region GET /api/v3/entries (with data)

    [Fact]
    public async Task GetEntries_WithSeededData_ReturnsV3WrappedResult()
    {
        var entries = Enumerable.Range(0, 3).Select(i => CreateSgvEntry(i, sgv: 100 + i * 10)).ToArray();
        await SeedSensorGlucose(entries);

        var response = await Client.GetAsync("/api/v3/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("mills", "date", "dateString", "sysTime", "srvModified", "srvCreated", "created_at");
    }

    #endregion

    #region GET /api/v3/entries?limit=3 (respects limit)

    [Fact]
    public async Task GetEntries_WithLimit3_RespectsLimit()
    {
        var entries = Enumerable.Range(0, 10).Select(i => CreateSgvEntry(i)).ToArray();
        await SeedSensorGlucose(entries);

        var response = await Client.GetAsync("/api/v3/entries?limit=3");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("mills", "date", "dateString", "sysTime", "srvModified", "srvCreated", "created_at");
    }

    #endregion

    #region GET /api/v3/entries?limit=-1 (negative limit error)

    [Fact]
    public async Task GetEntries_WithNegativeLimit_ReturnsErrorResponse()
    {
        var response = await Client.GetAsync("/api/v3/entries?limit=-1");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region POST /api/v3/entries (create)

    [Fact]
    public async Task PostEntry_SingleEntry_ReturnsCreatedV3ResponseShape()
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

        var response = await PostJsonAsync("/api/v3/entries", payload);
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("_id", "identifier", "lastModified", "mills", "date", "srvModified", "srvCreated", "sysTime", "created_at", "dateString");
    }

    #endregion

    #region POST then GET by identifier (AndroidAPS)

    [Fact]
    public async Task PostThenGet_AapsSgv_RoundTripsEveryUploadedField()
    {
        var payload = new
        {
            app = "AAPS",
            date = 1768563315863L,
            device = "Libre3",
            direction = "Flat",
            filtered = 0,
            isReadOnly = false,
            isValid = true,
            sgv = 100,
            type = "sgv",
            unfiltered = 0,
            units = "mg/dl",
            utcOffset = 120,
        };

        var (posted, fetched) = await PostThenGetV3Async("entries", payload);

        await Verify(new { Posted = posted, Fetched = fetched })
            .DontScrubDateTimes()
            .ScrubMembers("_id", "identifier", "lastModified", "srvModified", "srvCreated");
    }

    [Fact]
    public async Task PostThenGet_AapsMbg_RoundTripsEveryUploadedField()
    {
        var payload = new
        {
            app = "AAPS",
            date = 1768563315863L,
            device = "AndroidAPS-Manual",
            isReadOnly = false,
            isValid = true,
            mbg = 100,
            type = "mbg",
            units = "mg/dl",
            utcOffset = 120,
        };

        var (posted, fetched) = await PostThenGetV3Async("entries", payload);

        await Verify(new { Posted = posted, Fetched = fetched })
            .DontScrubDateTimes()
            .ScrubMembers("_id", "identifier", "lastModified", "srvModified", "srvCreated");
    }

    [Fact]
    public async Task PostThenGet_EntryWithItsOwnCreatedAt_KeepsIt()
    {
        var payload = new
        {
            app = "AAPS",
            created_at = "2026-01-16T10:00:00.000Z",
            device = "Libre3",
            sgv = 100,
            type = "sgv",
        };

        var (_, fetched) = await PostThenGetV3Async("entries", payload);

        await Verify(fetched)
            .DontScrubDateTimes()
            .ScrubMembers("_id", "identifier", "srvModified", "srvCreated");
    }

    [Fact]
    public async Task Put_AapsSgv_AnswersStatusAndLastModified()
    {
        var payload = new { app = "AAPS", date = 1768563315863L, device = "Libre3", sgv = 100, type = "sgv" };

        var replaced = await PostThenWriteV3Async("entries", payload, HttpMethod.Put, new { app = "AAPS", date = 1768563315863L, device = "Libre3", sgv = 110, type = "sgv" });

        await Verify(replaced).ScrubMembers("lastModified");
    }

    [Fact]
    public async Task Patch_AapsSgv_AnswersStatusOnly()
    {
        var payload = new { app = "AAPS", date = 1768563315863L, device = "Libre3", sgv = 100, type = "sgv" };

        var patched = await PostThenWriteV3Async("entries", payload, HttpMethod.Patch, new { sgv = 110 });

        await Verify(patched);
    }

    #endregion

    #region GET /api/v3/entries — V3 response includes identifier, srvModified, srvCreated

    [Fact]
    public async Task GetEntries_ResponseIncludesV3Fields()
    {
        await SeedSensorGlucose(CreateSgvEntry(0, sgv: 145, direction: "FortyFiveUp"));

        var response = await Client.GetAsync("/api/v3/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("mills", "date", "dateString", "sysTime", "srvModified", "srvCreated", "created_at");
    }

    #endregion
}
