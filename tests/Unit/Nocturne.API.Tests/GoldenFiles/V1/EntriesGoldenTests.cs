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

    #endregion
}
