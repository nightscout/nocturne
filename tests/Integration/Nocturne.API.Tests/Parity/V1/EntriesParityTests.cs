using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/entries endpoints.
/// Verifies that Nocturne responses match Nightscout 15.0.3 exactly.
/// </summary>
public class EntriesParityTests : ParityTestBase
{
    public EntriesParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region GET /api/v1/entries

    [Fact]
    public async Task GetEntries_Empty_ReturnsSameShape()
    {
        // No data seeded - both should return empty array
        await AssertGetParityAsync("/api/v1/entries");
    }

    [Fact]
    public async Task GetEntries_WithData_ReturnsSameShape()
    {
        // Seed identical data to both systems
        await SeedEntrySequenceAsync(count: 5);

        await AssertGetParityAsync("/api/v1/entries");
    }

    [Fact]
    public async Task GetEntries_WithCount_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/entries?count=3");
    }

    [Fact]
    public async Task GetEntries_WithType_ReturnsSameShape()
    {
        // Seed mixed entry types
        var entries = TestDataFactory.CreateMixedEntryTypes(
            includeBloodGlucose: true,
            includeMeterBg: true,
            includeCalibration: true);
        await SeedEntriesAsync(entries);

        await AssertGetParityAsync("/api/v1/entries?type=sgv");
        await AssertGetParityAsync("/api/v1/entries?type=mbg");
    }

    [Fact]
    public async Task GetEntries_WithFindFilter_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        // Test various find filters
        await AssertGetParityAsync("/api/v1/entries?find[sgv][$gte]=100");
        await AssertGetParityAsync("/api/v1/entries?find[sgv][$lte]=130");
    }

    [Fact]
    public async Task GetEntries_WithCountAndType_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        await AssertGetParityAsync("/api/v1/entries?count=5&type=sgv");
    }

    #endregion

    #region GET /api/v1/entries/current

    [Fact]
    public async Task GetEntriesCurrent_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/entries/current");
    }

    [Fact]
    public async Task GetEntriesCurrent_WithData_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await AssertGetParityAsync("/api/v1/entries/current");
    }

    #endregion

    #region GET /api/v1/entries/{spec}

    [Fact]
    public async Task GetEntriesByType_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);

        // Spec as type
        await AssertGetParityAsync("/api/v1/entries/sgv");
    }

    #endregion

    #region POST /api/v1/entries

    [Fact]
    public async Task PostEntries_Single_ReturnsSameShape()
    {
        var entry = TestDataFactory.CreateEntry(sgv: 150);

        // Convert to format both systems accept
        var payload = new
        {
            type = entry.Type,
            sgv = entry.Sgv,
            direction = entry.Direction,
            device = entry.Device,
            date = entry.Mills,
            dateString = entry.DateString
        };

        await AssertPostParityAsync("/api/v1/entries", new[] { payload });
    }

    [Fact]
    public async Task PostEntries_Batch_ReturnsSameShape()
    {
        var entries = TestDataFactory.CreateEntrySequence(count: 3);

        var payload = entries.Select(e => new
        {
            type = e.Type,
            sgv = e.Sgv,
            direction = e.Direction,
            device = e.Device,
            date = e.Mills,
            dateString = e.DateString
        }).ToArray();

        await AssertPostParityAsync("/api/v1/entries", payload);
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetEntries_InvalidCount_ReturnsSameShape()
    {
        // Negative count - should both handle gracefully
        await AssertGetParityAsync("/api/v1/entries?count=-1");
    }

    [Fact]
    public async Task GetEntries_InvalidType_ReturnsSameShape()
    {
        // Unknown type - both should return empty or error consistently
        await AssertGetParityAsync("/api/v1/entries?type=nonexistent");
    }

    #endregion
}
