using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/ddata endpoints.
/// V2 DData API provides direct data access:
/// - GET /api/v2/ddata - Current DData structure
/// - GET /api/v2/ddata/at[/{timestamp}] - Full DData at a time, now when omitted
/// - GET /api/v2/ddata/raw - Raw DData without filtering
/// </summary>
[Collection(ParityShards.Two)]
public class DDataParityTests : ParityTestBase
{
    public DDataParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions()
    {
        // DData responses contain dynamic timestamps and computed values. Nocturne serves no store
        // size, a fixed 0, where Nightscout serves Mongo's byte counts, so only their presence is checked.
        return ComparisonOptions.Default
            .WithIgnoredFields("lastUpdated", "serverTime", "serverTimeEpoch", "now")
            .WithPresenceOnlyFields("dataSize", "indexSize");
    }

    #region Helper Methods

    private async Task SeedProfileForTestAsync()
    {
        var profile = TestDataFactory.CreateProfile();
        var nsProfile = new
        {
            defaultProfile = profile.DefaultProfile,
            startDate = profile.StartDate,
            mills = profile.Mills,
            units = profile.Units,
            store = profile.Store
        };

        await NightscoutClient.PostAsJsonAsync("/api/v1/profile", nsProfile);
        await NocturneClient.PostAsJsonAsync("/api/v1/profile", profile);
    }

    #endregion

    #region GET /api/v2/ddata

    [Fact]
    public async Task GetDData_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/ddata");
    }

    [Fact]
    public async Task GetDData_WithEntries_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v2/ddata");
    }

    [Fact]
    public async Task GetDData_WithTreatments_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-2))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/ddata");
    }

    [Fact]
    public async Task GetDData_WithDeviceStatus_ReturnsSameShape()
    {
        var deviceStatuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(
                device: "openaps://TestPump",
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-5)),
            TestDataFactory.CreateDeviceStatus(
                device: "loop://TestLoop",
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-10))
        };
        await SeedDeviceStatusAsync(deviceStatuses);

        await AssertGetParityAsync("/api/v2/ddata");
    }

    [Fact]
    public async Task GetDData_WithProfile_ReturnsSameShape()
    {
        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/ddata");
    }

    [Fact]
    public async Task GetDData_WithAllDataTypes_ReturnsSameShape()
    {
        // Seed all data types for comprehensive test
        await SeedEntrySequenceAsync(count: 10);

        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);

        var deviceStatuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(
                device: "openaps://TestPump",
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-5))
        };
        await SeedDeviceStatusAsync(deviceStatuses);

        await SeedProfileForTestAsync();

        await AssertGetParityAsync("/api/v2/ddata");
    }

    #endregion

    #region GET /api/v2/ddata/at/{timestamp}

    [Fact]
    public async Task GetDDataAt_UnixTimestamp_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);
        var timestamp = TestTimeProvider.GetTestTime().AddHours(-1).ToUnixTimeMilliseconds();

        await AssertGetParityAsync($"/api/v2/ddata/at/{timestamp}");
    }

    [Fact]
    public async Task GetDDataAt_IsoDateString_ReturnsSameShape()
    {
        // Nightscout answers an earlier time from a copy of its in-memory data that it refills from
        // Mongo, so the wait makes that copy start from the cleaned data, not the previous test's.
        // The other tests here are ParityDivergences, which compare nothing with Nightscout.
        await WaitForNightscoutEmptyAsync();
        var at = TestTimeProvider.GetTestTime().AddHours(-1);
        await SeedEntriesAsync(TestDataFactory.CreateEntrySequence(5, 5, startTime: at));
        var isoDate = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        await AssertGetParityAsync($"/api/v2/ddata/at/{Uri.EscapeDataString(isoDate)}");
    }

    [Fact]
    public async Task GetDDataAt_InvalidTimestamp_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/ddata/at/invalid");
    }

    [Fact]
    public async Task GetDDataAt_FutureTimestamp_ReturnsSameShape()
    {
        var futureTimestamp = TestTimeProvider.GetTestTime().AddDays(365 * 2).ToUnixTimeMilliseconds();

        await AssertGetParityAsync($"/api/v2/ddata/at/{futureTimestamp}");
    }

    #endregion

    #region GET /api/v2/ddata/raw

    [Fact]
    public async Task GetRawDData_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/ddata/raw");
    }

    [Fact]
    public async Task GetRawDData_WithData_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v2/ddata/raw");
    }

    [Fact]
    public async Task GetRawDData_WithTimestamp_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);
        var timestamp = TestTimeProvider.GetTestTime().AddHours(-1).ToUnixTimeMilliseconds();

        await AssertGetParityAsync($"/api/v2/ddata/raw?timestamp={timestamp}");
    }

    #endregion
}
