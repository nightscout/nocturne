using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/settings endpoints.
/// V3 API follows a generic CRUD pattern with:
/// - SEARCH: GET /settings
/// - CREATE: POST /settings
/// - READ: GET /settings/{id}
/// - UPDATE: PUT /settings/{id}
/// - DELETE: DELETE /settings/{id}
/// </summary>
public class SettingsParityTests : ParityTestBase
{
    public SettingsParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region Data Seeding

    private async Task SeedSettingsV3Async(params object[] settings)
    {
        foreach (var setting in settings)
        {
            await NightscoutClient.PostAsJsonAsync("/api/v3/settings", setting);
            await NocturneClient.PostAsJsonAsync("/api/v3/settings", setting);
        }
    }

    #endregion

    #region SEARCH - GET /api/v3/settings

    [Fact]
    public async Task Search_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/settings");
    }

    [Fact]
    public async Task Search_WithData_ReturnsSameShape()
    {
        var settings = new object[]
        {
            new
            {
                type = "thresholds",
                value = new
                {
                    bgHigh = 260,
                    bgLow = 55,
                    bgTargetTop = 180,
                    bgTargetBottom = 80
                }
            }
        };
        await SeedSettingsV3Async(settings);

        await AssertGetParityAsync("/api/v3/settings");
    }

    [Fact]
    public async Task Search_WithLimit_ReturnsSameShape()
    {
        var settings = Enumerable.Range(0, 5)
            .Select(i => (object)new
            {
                type = $"setting-{i}",
                value = new { key = $"value-{i}" }
            })
            .ToArray();
        await SeedSettingsV3Async(settings);

        await AssertGetParityAsync("/api/v3/settings?limit=3");
    }

    [Fact]
    public async Task Search_Filter_Type_ReturnsSameShape()
    {
        var settings = new object[]
        {
            new { type = "thresholds", value = new { bgHigh = 260 } },
            new { type = "alarms", value = new { enabled = true } },
            new { type = "thresholds", value = new { bgLow = 55 } }
        };
        await SeedSettingsV3Async(settings);

        await AssertGetParityAsync("/api/v3/settings?type$eq=thresholds");
    }

    #endregion

    #region CREATE - POST /api/v3/settings

    [Fact]
    public async Task Create_Thresholds_ReturnsSameShape()
    {
        var settings = new
        {
            type = "thresholds",
            value = new
            {
                bgHigh = 260,
                bgLow = 55,
                bgTargetTop = 180,
                bgTargetBottom = 80
            }
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    [Fact]
    public async Task Create_Alarms_ReturnsSameShape()
    {
        var settings = new
        {
            type = "alarms",
            value = new
            {
                enabled = true,
                urgent = new
                {
                    high = true,
                    low = true
                },
                warn = new
                {
                    high = true,
                    low = false
                }
            }
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    [Fact]
    public async Task Create_Units_ReturnsSameShape()
    {
        var settings = new
        {
            type = "units",
            value = new
            {
                bg = "mg/dL"
            }
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    [Fact]
    public async Task Create_TimeFormat_ReturnsSameShape()
    {
        var settings = new
        {
            type = "timeFormat",
            value = new
            {
                format = 24
            }
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    [Fact]
    public async Task Create_CustomSetting_ReturnsSameShape()
    {
        var settings = new
        {
            type = "custom",
            value = new
            {
                customKey = "customValue",
                nested = new
                {
                    key1 = "value1",
                    key2 = 42
                }
            }
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    #endregion

    #region READ - GET /api/v3/settings/{id}

    [Fact]
    public async Task Read_NotFound_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/settings/nonexistent123456789012");
    }

    #endregion

    #region UPDATE - PUT /api/v3/settings/{id}

    [Fact]
    public async Task Update_NotFound_ReturnsSameShape()
    {
        var settings = new
        {
            type = "thresholds",
            value = new { bgHigh = 280 }
        };

        await AssertPutParityAsync("/api/v3/settings/nonexistent123456789012", settings);
    }

    #endregion

    #region DELETE - DELETE /api/v3/settings/{id}

    [Fact]
    public async Task Delete_NotFound_ReturnsSameShape()
    {
        await AssertDeleteParityAsync("/api/v3/settings/nonexistent123456789012");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task Create_Empty_ReturnsSameShape()
    {
        var settings = new { };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    [Fact]
    public async Task Create_MissingType_ReturnsSameShape()
    {
        var settings = new
        {
            value = new { someKey = "someValue" }
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    [Fact]
    public async Task Create_MissingValue_ReturnsSameShape()
    {
        var settings = new
        {
            type = "empty"
        };

        await AssertPostParityAsync("/api/v3/settings", settings);
    }

    #endregion
}
