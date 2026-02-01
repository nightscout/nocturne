using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/count/* endpoints.
/// Covers: GET/entries/where, GET/treatments/where, GET/devicestatus/where,
///         GET/activity/where, GET/{storage}/where
/// </summary>
public class CountParityTests : ParityTestBase
{
    public CountParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region GET /api/v1/count/entries/where

    [Fact]
    public async Task CountEntries_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/count/entries/where");
    }

    [Fact]
    public async Task CountEntries_WithData_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/count/entries/where");
    }

    [Fact]
    public async Task CountEntries_WithTypeFilter_ReturnsSameShape()
    {
        var entries = TestDataFactory.CreateMixedEntryTypes(
            includeBloodGlucose: true,
            includeMeterBg: true,
            includeCalibration: true);
        await SeedEntriesAsync(entries);

        await AssertGetParityAsync("/api/v1/count/entries/where?find[type]=sgv");
        await AssertGetParityAsync("/api/v1/count/entries/where?find[type]=mbg");
    }

    [Fact]
    public async Task CountEntries_WithSgvFilter_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/count/entries/where?find[sgv][$gte]=100");
        await AssertGetParityAsync("/api/v1/count/entries/where?find[sgv][$lte]=130");
    }

    [Fact]
    public async Task CountEntries_WithDateFilter_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        var oneHourAgo = TestTimeProvider.GetTestTime().AddHours(-1).ToUnixTimeMilliseconds();
        await AssertGetParityAsync($"/api/v1/count/entries/where?find[date][$gte]={oneHourAgo}");
    }

    #endregion

    #region GET /api/v1/count/treatments/where

    [Fact]
    public async Task CountTreatments_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/count/treatments/where");
    }

    [Fact]
    public async Task CountTreatments_WithData_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus", insulin: 2.0),
            TestDataFactory.CreateTreatment(eventType: "Meal Bolus", insulin: 4.0),
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus", insulin: 1.5)
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v1/count/treatments/where");
    }

    [Fact]
    public async Task CountTreatments_WithEventTypeFilter_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Meal Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus")
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v1/count/treatments/where?find[eventType]=Correction%20Bolus");
    }

    #endregion

    #region GET /api/v1/count/devicestatus/where

    [Fact]
    public async Task CountDeviceStatus_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/count/devicestatus/where");
    }

    [Fact]
    public async Task CountDeviceStatus_WithData_ReturnsSameShape()
    {
        var statuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(device: "loop://device1"),
            TestDataFactory.CreateDeviceStatus(device: "loop://device2"),
            TestDataFactory.CreateDeviceStatus(device: "openaps://device3")
        };
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v1/count/devicestatus/where");
    }

    [Fact]
    public async Task CountDeviceStatus_WithDeviceFilter_ReturnsSameShape()
    {
        var statuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(device: "loop://iphone"),
            TestDataFactory.CreateDeviceStatus(device: "loop://iphone"),
            TestDataFactory.CreateDeviceStatus(device: "openaps://rpi")
        };
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v1/count/devicestatus/where?find[device]=loop://iphone");
    }

    #endregion

    #region GET /api/v1/count/activity/where

    [Fact]
    public async Task CountActivity_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/count/activity/where");
    }

    #endregion

    #region GET /api/v1/count/{storage}/where (generic)

    [Theory]
    [InlineData("entries")]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [InlineData("food")]
    [InlineData("profile")]
    public async Task CountGeneric_Empty_ReturnsSameShape(string storage)
    {
        await AssertGetParityAsync($"/api/v1/count/{storage}/where");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task CountEntries_InvalidFilter_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/count/entries/where?find[invalid]=value");
    }

    [Fact]
    public async Task CountGeneric_InvalidStorage_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/count/nonexistent/where");
    }

    #endregion
}
