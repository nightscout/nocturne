using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.GoldenFiles;

/// <summary>
/// Golden file tests for error response shapes across API versions.
/// Verifies that V1 and V3 endpoints return the correct error/empty response formats
/// for client compatibility.
/// </summary>
public class ErrorResponseGoldenTests : GoldenFileTestBase
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public ErrorResponseGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region V1 Empty Results

    [Fact]
    public async Task V1_GetEntriesCurrent_WithNoData_ReturnsEmptyArray()
    {
        // No seeding - empty database
        // V1 /current returns 200 with empty array when no entries exist
        var response = await Client.GetAsync("/api/v1/entries/current");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task V1_GetEntries_WithNoData_ReturnsEmptyArray()
    {
        // No seeding - empty database
        // V1 /entries returns 200 with empty array when no entries exist
        var response = await Client.GetAsync("/api/v1/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region V3 Validation Errors

    [Fact]
    public async Task V3_PostEntry_WithEmptyBody_ReturnsErrorShape()
    {
        // POST with an empty JSON object - missing required fields
        var response = await PostJsonAsync("/api/v3/entries", new { });
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("_id", "identifier", "lastModified", "mills", "date", "srvModified", "srvCreated", "sysTime", "created_at", "dateString");
    }

    [Fact]
    public async Task V3_PostEntry_WithMissingRequiredFields_ReturnsErrorShape()
    {
        // POST with partial data - has sgv but missing date and app fields
        var payload = new
        {
            type = "sgv",
            sgv = 120,
        };

        var response = await PostJsonAsync("/api/v3/entries", payload);
        var captured = await CaptureResponse(response);

        await Verify(captured)
            .ScrubMembers("_id", "identifier", "lastModified", "mills", "date", "srvModified", "srvCreated", "sysTime", "created_at", "dateString");
    }

    #endregion

    #region V3 404 for Non-Existent Resource

    [Fact]
    public async Task V3_GetEntryById_NonExistent_Returns404Shape()
    {
        // Request a non-existent entry ID
        var response = await Client.GetAsync("/api/v3/entries/nonexistent-id-that-does-not-exist");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region V3 Parameter Validation

    [Fact]
    public async Task V3_GetEntries_WithNegativeLimit_ReturnsErrorShape()
    {
        // Nightscout V3 API returns error for negative limit
        var response = await Client.GetAsync("/api/v3/entries?limit=-1");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion

    #region V1 vs V3 Format Differences

    [Fact]
    public async Task V1_GetEntries_EmptyDb_ReturnsPlainArray()
    {
        // V1 returns a plain JSON array for collection endpoints
        var response = await Client.GetAsync("/api/v1/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task V3_GetEntries_EmptyDb_ReturnsWrappedObject()
    {
        // V3 returns a wrapped object {status: 200, result: [...]} for collection endpoints
        var response = await Client.GetAsync("/api/v3/entries");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    #endregion
}
