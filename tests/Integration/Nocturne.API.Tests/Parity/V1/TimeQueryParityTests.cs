using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/times/* and /api/v1/slice/* endpoints.
/// Covers time-based queries and data slicing operations.
/// </summary>
public class TimeQueryParityTests : ParityTestBase
{
    public TimeQueryParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region GET /api/v1/times

    [Fact]
    public async Task GetTimes_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/times");
    }

    [Fact]
    public async Task GetTimes_WithData_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/times");
    }

    #endregion

    #region GET /api/v1/times/{prefix}

    [Fact]
    public async Task GetTimesWithPrefix_Entries_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v1/times/entries");
    }

    [Fact]
    public async Task GetTimesWithPrefix_Treatments_ReturnsSameShape()
    {
        var treatment = TestDataFactory.CreateTreatment();
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v1/times/treatments");
    }

    #endregion

    #region GET /api/v1/times/{prefix}/{regex}

    [Fact]
    public async Task GetTimesWithRegex_AllEntries_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v1/times/entries/.*");
    }

    [Fact]
    public async Task GetTimesWithRegex_SgvOnly_ReturnsSameShape()
    {
        var entries = TestDataFactory.CreateMixedEntryTypes();
        await SeedEntriesAsync(entries);

        await AssertGetParityAsync("/api/v1/times/entries/sgv");
    }

    [Fact]
    public async Task GetTimesWithRegex_WithDateRange_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        var twoHoursAgo = TestTimeProvider.GetTestTime().AddHours(-2);
        var prefix = twoHoursAgo.ToString("yyyy-MM-ddTHH");

        await AssertGetParityAsync($"/api/v1/times/entries/{prefix}");
    }

    #endregion

    #region GET /api/v1/times/echo

    [Fact]
    public async Task GetTimesEcho_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/times/echo");
    }

    [Fact]
    public async Task GetTimesEchoWithPrefix_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/times/echo/entries");
    }

    [Fact]
    public async Task GetTimesEchoWithRegex_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/times/echo/entries/.*");
    }

    #endregion

    #region GET /api/v1/slice/{storage}/{field}

    [Fact]
    public async Task GetSlice_EntriesDate_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v1/slice/entries/date");
    }

    [Fact]
    public async Task GetSlice_EntriesSgv_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v1/slice/entries/sgv");
    }

    [Fact]
    public async Task GetSlice_TreatmentsCreatedAt_ReturnsSameShape()
    {
        var treatment = TestDataFactory.CreateTreatment();
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v1/slice/treatments/created_at");
    }

    #endregion

    #region GET /api/v1/slice/{storage}/{field}/{type}

    [Fact]
    public async Task GetSliceWithType_EntriesDateSgv_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v1/slice/entries/date/sgv");
    }

    [Fact]
    public async Task GetSliceWithType_EntriesDateMbg_ReturnsSameShape()
    {
        var entries = TestDataFactory.CreateMixedEntryTypes();
        await SeedEntriesAsync(entries);

        await AssertGetParityAsync("/api/v1/slice/entries/date/mbg");
    }

    #endregion

    #region GET /api/v1/slice/{storage}/{field}/{type}/{prefix}

    [Fact]
    public async Task GetSliceWithPrefix_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        var prefix = TestTimeProvider.GetTestTime().ToString("yyyy-MM-dd");
        await AssertGetParityAsync($"/api/v1/slice/entries/date/sgv/{prefix}");
    }

    #endregion

    #region GET /api/v1/slice/{storage}/{field}/{type}/{prefix}/{regex}

    [Fact]
    public async Task GetSliceWithRegex_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        var prefix = TestTimeProvider.GetTestTime().ToString("yyyy-MM-dd");
        await AssertGetParityAsync($"/api/v1/slice/entries/date/sgv/{prefix}/T.*");
    }

    #endregion

    #region Query Parameters

    [Fact]
    public async Task GetTimes_WithCount_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/times/entries?count=5");
    }

    [Fact]
    public async Task GetSlice_WithCount_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/slice/entries/date?count=5");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetSlice_InvalidStorage_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/slice/nonexistent/date");
    }

    [Fact]
    public async Task GetSlice_InvalidField_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/slice/entries/nonexistent");
    }

    #endregion
}
