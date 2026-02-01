using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/profile endpoints.
/// Covers: GET, POST, GET/current, GET/{spec}
/// </summary>
public class ProfileParityTests : ParityTestBase
{
    public ProfileParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// Override comparison options to ignore timeAsSeconds field.
    /// Nocturne calculates this value while Nightscout returns null.
    /// The value is correctly computed so this is acceptable for parity.
    /// </summary>
    protected override ComparisonOptions GetComparisonOptions()
    {
        return ComparisonOptions.Default.WithIgnoredFields("timeAsSeconds");
    }

    #region Data Seeding

    private async Task SeedProfileAsync(Profile profile)
    {
        // Convert to Nightscout format
        var nsProfile = new
        {
            defaultProfile = profile.DefaultProfile,
            startDate = profile.StartDate,
            mills = profile.Mills,
            units = profile.Units,
            store = profile.Store
        };

        var nsResponse = await NightscoutClient.PostAsJsonAsync("/api/v1/profile", nsProfile);
        nsResponse.EnsureSuccessStatusCode();

        var nocResponse = await NocturneClient.PostAsJsonAsync("/api/v1/profile", profile);
        nocResponse.EnsureSuccessStatusCode();
    }

    #endregion

    #region GET /api/v1/profile

    [Fact]
    public async Task GetProfile_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/profile");
    }

    [Fact]
    public async Task GetProfile_WithData_ReturnsSameShape()
    {
        var profile = TestDataFactory.CreateProfile();
        await SeedProfileAsync(profile);

        await AssertGetParityAsync("/api/v1/profile");
    }

    [Fact]
    public async Task GetProfile_MultipleProfiles_ReturnsSameShape()
    {
        var profile1 = TestDataFactory.CreateProfile(defaultProfile: "Day");
        var profile2 = TestDataFactory.CreateProfile(
            defaultProfile: "Night");

        await SeedProfileAsync(profile1);
        await SeedProfileAsync(profile2);

        await AssertGetParityAsync("/api/v1/profile");
    }

    #endregion

    #region GET /api/v1/profile/current

    [Fact]
    public async Task GetProfileCurrent_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/profile/current");
    }

    [Fact]
    public async Task GetProfileCurrent_WithData_ReturnsSameShape()
    {
        var profile = TestDataFactory.CreateProfile();
        await SeedProfileAsync(profile);

        await AssertGetParityAsync("/api/v1/profile/current");
    }

    #endregion

    #region POST /api/v1/profile

    [Fact]
    public async Task PostProfile_Simple_ReturnsSameShape()
    {
        var profile = new
        {
            defaultProfile = "Test Profile",
            startDate = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = TestTimeProvider.GetTestTime().ToUnixTimeMilliseconds(),
            units = "mg/dL",
            store = new Dictionary<string, object>
            {
                ["Test Profile"] = new
                {
                    dia = 3.0,
                    carbs_hr = 20,
                    delay = 20,
                    timezone = "UTC",
                    units = "mg/dL",
                    basal = new[]
                    {
                        new { time = "00:00", value = 0.5 }
                    },
                    carbratio = new[]
                    {
                        new { time = "00:00", value = 15.0 }
                    },
                    sens = new[]
                    {
                        new { time = "00:00", value = 100.0 }
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

        await AssertPostParityAsync("/api/v1/profile", profile);
    }

    [Fact]
    public async Task PostProfile_WithMultipleTimePeriods_ReturnsSameShape()
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
                    dia = 4.0,
                    carbs_hr = 25,
                    delay = 20,
                    timezone = "America/New_York",
                    units = "mg/dL",
                    basal = new[]
                    {
                        new { time = "00:00", value = 0.5 },
                        new { time = "06:00", value = 0.7 },
                        new { time = "09:00", value = 0.8 },
                        new { time = "21:00", value = 0.6 }
                    },
                    carbratio = new[]
                    {
                        new { time = "00:00", value = 18.0 },
                        new { time = "07:00", value = 12.0 },
                        new { time = "12:00", value = 15.0 },
                        new { time = "18:00", value = 18.0 }
                    },
                    sens = new[]
                    {
                        new { time = "00:00", value = 100.0 },
                        new { time = "06:00", value = 95.0 },
                        new { time = "09:00", value = 100.0 },
                        new { time = "17:00", value = 110.0 }
                    },
                    target_low = new[]
                    {
                        new { time = "00:00", value = 90.0 },
                        new { time = "06:00", value = 100.0 }
                    },
                    target_high = new[]
                    {
                        new { time = "00:00", value = 160.0 },
                        new { time = "06:00", value = 140.0 }
                    }
                }
            }
        };

        await AssertPostParityAsync("/api/v1/profile", profile);
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetProfile_NonExistentId_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/profile/nonexistent123");
    }

    #endregion
}
