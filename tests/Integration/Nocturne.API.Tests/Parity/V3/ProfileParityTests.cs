using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/profile endpoints.
/// V3 API follows a generic CRUD pattern with:
/// - SEARCH: GET /profile
/// - CREATE: POST /profile
/// - READ: GET /profile/{id}
/// - UPDATE: PUT /profile/{id}
/// - DELETE: DELETE /profile/{id}
/// </summary>
public class ProfileParityTests : ParityTestBase
{
    public ProfileParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region Data Seeding

    private async Task SeedProfileV3Async(params object[] profiles)
    {
        foreach (var profile in profiles)
        {
            await NightscoutClient.PostAsJsonAsync("/api/v3/profile", profile);
            await NocturneClient.PostAsJsonAsync("/api/v3/profile", profile);
        }
    }

    private object CreateTestProfile(string name = "Test Profile")
    {
        return new
        {
            defaultProfile = name,
            startDate = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = TestTimeProvider.GetTestTime().ToUnixTimeMilliseconds(),
            units = "mg/dL",
            store = new Dictionary<string, object>
            {
                [name] = new
                {
                    dia = 3.0,
                    carbs_hr = 20,
                    delay = 20,
                    timezone = "UTC",
                    units = "mg/dL",
                    basal = new[]
                    {
                        new { time = "00:00", value = 0.5 },
                        new { time = "06:00", value = 0.7 },
                        new { time = "12:00", value = 0.6 },
                        new { time = "18:00", value = 0.5 }
                    },
                    carbratio = new[]
                    {
                        new { time = "00:00", value = 15.0 },
                        new { time = "06:00", value = 12.0 },
                        new { time = "12:00", value = 15.0 },
                        new { time = "18:00", value = 18.0 }
                    },
                    sens = new[]
                    {
                        new { time = "00:00", value = 100.0 },
                        new { time = "06:00", value = 90.0 },
                        new { time = "12:00", value = 100.0 },
                        new { time = "18:00", value = 110.0 }
                    },
                    target_low = new[]
                    {
                        new { time = "00:00", value = 100.0 }
                    },
                    target_high = new[]
                    {
                        new { time = "00:00", value = 180.0 }
                    }
                }
            }
        };
    }

    #endregion

    #region SEARCH - GET /api/v3/profile

    [Fact]
    public async Task Search_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/profile");
    }

    [Fact]
    public async Task Search_WithData_ReturnsSameShape()
    {
        await SeedProfileV3Async(CreateTestProfile("Day Profile"));

        await AssertGetParityAsync("/api/v3/profile");
    }

    [Fact]
    public async Task Search_MultipleProfiles_ReturnsSameShape()
    {
        await SeedProfileV3Async(
            CreateTestProfile("Day Profile"),
            CreateTestProfile("Night Profile"));

        await AssertGetParityAsync("/api/v3/profile");
    }

    [Fact]
    public async Task Search_WithLimit_ReturnsSameShape()
    {
        await SeedProfileV3Async(
            CreateTestProfile("Profile 1"),
            CreateTestProfile("Profile 2"),
            CreateTestProfile("Profile 3"));

        await AssertGetParityAsync("/api/v3/profile?limit=2");
    }

    [Fact]
    public async Task Search_WithSort_ReturnsSameShape()
    {
        await SeedProfileV3Async(
            CreateTestProfile("Alpha"),
            CreateTestProfile("Beta"));

        await AssertGetParityAsync("/api/v3/profile?sort=defaultProfile");
        await AssertGetParityAsync("/api/v3/profile?sort$desc=defaultProfile");
    }

    #endregion

    #region CREATE - POST /api/v3/profile

    [Fact]
    public async Task Create_Simple_ReturnsSameShape()
    {
        var profile = CreateTestProfile("New Profile");

        await AssertPostParityAsync("/api/v3/profile", profile);
    }

    [Fact]
    public async Task Create_WithMmol_ReturnsSameShape()
    {
        var profile = new
        {
            defaultProfile = "Mmol Profile",
            startDate = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = TestTimeProvider.GetTestTime().ToUnixTimeMilliseconds(),
            units = "mmol",
            store = new Dictionary<string, object>
            {
                ["Mmol Profile"] = new
                {
                    dia = 4.0,
                    carbs_hr = 25,
                    delay = 20,
                    timezone = "Europe/London",
                    units = "mmol",
                    basal = new[]
                    {
                        new { time = "00:00", value = 0.6 }
                    },
                    carbratio = new[]
                    {
                        new { time = "00:00", value = 10.0 }
                    },
                    sens = new[]
                    {
                        new { time = "00:00", value = 3.0 }
                    },
                    target_low = new[]
                    {
                        new { time = "00:00", value = 5.0 }
                    },
                    target_high = new[]
                    {
                        new { time = "00:00", value = 8.0 }
                    }
                }
            }
        };

        await AssertPostParityAsync("/api/v3/profile", profile);
    }

    [Fact]
    public async Task Create_WithMultipleTimePeriods_ReturnsSameShape()
    {
        var profile = new
        {
            defaultProfile = "Complex Profile",
            startDate = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = TestTimeProvider.GetTestTime().ToUnixTimeMilliseconds(),
            units = "mg/dL",
            store = new Dictionary<string, object>
            {
                ["Complex Profile"] = new
                {
                    dia = 4.5,
                    carbs_hr = 30,
                    delay = 15,
                    timezone = "America/New_York",
                    units = "mg/dL",
                    basal = new[]
                    {
                        new { time = "00:00", value = 0.4 },
                        new { time = "03:00", value = 0.5 },
                        new { time = "06:00", value = 0.7 },
                        new { time = "09:00", value = 0.6 },
                        new { time = "12:00", value = 0.5 },
                        new { time = "15:00", value = 0.6 },
                        new { time = "18:00", value = 0.5 },
                        new { time = "21:00", value = 0.4 }
                    },
                    carbratio = new[]
                    {
                        new { time = "00:00", value = 18.0 },
                        new { time = "06:00", value = 10.0 },
                        new { time = "12:00", value = 14.0 },
                        new { time = "18:00", value = 16.0 }
                    },
                    sens = new[]
                    {
                        new { time = "00:00", value = 120.0 },
                        new { time = "06:00", value = 80.0 },
                        new { time = "12:00", value = 100.0 },
                        new { time = "18:00", value = 110.0 }
                    },
                    target_low = new[]
                    {
                        new { time = "00:00", value = 90.0 },
                        new { time = "06:00", value = 100.0 },
                        new { time = "22:00", value = 110.0 }
                    },
                    target_high = new[]
                    {
                        new { time = "00:00", value = 140.0 },
                        new { time = "06:00", value = 120.0 },
                        new { time = "22:00", value = 150.0 }
                    }
                }
            }
        };

        await AssertPostParityAsync("/api/v3/profile", profile);
    }

    #endregion

    #region READ - GET /api/v3/profile/{id}

    [Fact]
    public async Task Read_NotFound_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/profile/nonexistent123456789012");
    }

    #endregion

    #region UPDATE - PUT /api/v3/profile/{id}

    [Fact]
    public async Task Update_NotFound_ReturnsSameShape()
    {
        var profile = CreateTestProfile("Updated Profile");

        await AssertPutParityAsync("/api/v3/profile/nonexistent123456789012", profile);
    }

    #endregion

    #region DELETE - DELETE /api/v3/profile/{id}

    [Fact]
    public async Task Delete_NotFound_ReturnsSameShape()
    {
        await AssertDeleteParityAsync("/api/v3/profile/nonexistent123456789012");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task Create_Empty_ReturnsSameShape()
    {
        var profile = new { };

        await AssertPostParityAsync("/api/v3/profile", profile);
    }

    [Fact]
    public async Task Create_MissingStore_ReturnsSameShape()
    {
        var profile = new
        {
            defaultProfile = "Invalid Profile",
            startDate = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            units = "mg/dL"
        };

        await AssertPostParityAsync("/api/v3/profile", profile);
    }

    #endregion
}
