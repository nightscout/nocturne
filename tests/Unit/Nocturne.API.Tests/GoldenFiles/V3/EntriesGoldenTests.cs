using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;

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
            .ScrubMembers("_id", "identifier", "mills", "date", "srvModified", "srvCreated", "sysTime", "created_at", "dateString");
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
