using FluentAssertions;
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
    public async Task GetEntries_WithFindTypeFilter_ReturnsSameShape()
    {
        // find[type]=xxx is the query form that actually filters by type on the bare
        // /entries route. (Nightscout's query_models / find_options only ever read
        // query.find.type — never a top-level query.type.)
        var entries = TestDataFactory.CreateMixedEntryTypes(
            includeBloodGlucose: true,
            includeMeterBg: true,
            includeCalibration: true);
        await SeedEntriesAsync(entries);

        await AssertGetParityAsync("/api/v1/entries?find[type]=sgv");
        await AssertGetParityAsync("/api/v1/entries?find[type]=mbg");
    }

    [Fact]
    public async Task GetEntries_TopLevelTypeParam_IsIgnored_ReturnsSameShape()
    {
        // A bare ?type= query parameter is a NO-OP on Nightscout's /entries route:
        // the bare route never calls prepReqModel, so query.find stays undefined, and
        // neither the in-memory path nor find_options (lib/server/query.js) ever read a
        // top-level query.type. To filter by type you must use /entries/{spec} or
        // find[type]=xxx. This test locks in that Nocturne mirrors that quirk — the
        // param is silently ignored, so the response equals the unfiltered one.
        var entries = TestDataFactory.CreateMixedEntryTypes(
            includeBloodGlucose: true,
            includeMeterBg: true,
            includeCalibration: true);
        await SeedEntriesAsync(entries);

        // Both systems agree on the (unfiltered) response for a bare ?type= param.
        await AssertGetParityAsync("/api/v1/entries?type=sgv");
        await AssertGetParityAsync("/api/v1/entries?type=mbg");
        await AssertGetParityAsync("/api/v1/entries?type=nonexistent");

        // Cross-check within Nocturne: a no-op type param yields the exact same payload
        // as no param at all, proving it is not silently filtering entries out. (Same
        // stored entries in the same order => byte-identical bodies.)
        var unfiltered = await NocturneClient.GetStringAsync("/api/v1/entries");
        var withTypeParam = await NocturneClient.GetStringAsync("/api/v1/entries?type=mbg");
        withTypeParam.Should().Be(
            unfiltered,
            "a top-level ?type= param must be ignored on /entries, matching Nightscout");
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
    public async Task GetEntries_WithCountAndFindType_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 10);

        // count combines with the find[type]= filter form. (A bare ?type= would be
        // ignored — see GetEntries_TopLevelTypeParam_IsIgnored.)
        await AssertGetParityAsync("/api/v1/entries?count=5&find[type]=sgv");
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
        // The /entries/{spec} path segment is the canonical way to filter by type
        // (prepReqModel maps the spec into query.find.type).
        var entries = TestDataFactory.CreateMixedEntryTypes(
            includeBloodGlucose: true,
            includeMeterBg: true,
            includeCalibration: true);
        await SeedEntriesAsync(entries);

        await AssertGetParityAsync("/api/v1/entries/sgv");
        await AssertGetParityAsync("/api/v1/entries/mbg");
        await AssertGetParityAsync("/api/v1/entries/cal");
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
    public async Task GetEntries_UnknownFindType_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        // An unknown type via the filtering form (find[type]=) should yield an empty
        // array on both systems. (Contrast with a bare ?type=nonexistent, which is
        // ignored and returns the default entries — see
        // GetEntries_TopLevelTypeParam_IsIgnored.)
        await AssertGetParityAsync("/api/v1/entries?find[type]=nonexistent");
    }

    #endregion
}
