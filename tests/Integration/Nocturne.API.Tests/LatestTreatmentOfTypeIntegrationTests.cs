using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Treatments;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The latest treatment of one event type before a time, which a ddata frame carries beside its
/// windowed treatments, read from real rows. Each test anchors at its own time, years apart, so
/// the rows one test writes sit outside every window another reads.
/// </summary>
[Trait("Category", "Integration")]
public class LatestTreatmentOfTypeIntegrationTests : ApiIntegrationTestBase
{
    private const long Day = 24L * 60 * 60 * 1000;
    private const long Minute = 60_000;
    private const long ProfileSwitchWindow = 31 * 12 * Day;
    private const long EventWindow = 62 * Day;

    public LatestTreatmentOfTypeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static long Anchor(int year) => new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private async Task PostAsync(object treatment)
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/treatments", treatment);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string At(long mills) =>
        DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private Task PostSwitchAsync(long mills, string profile, int duration) =>
        PostAsync(new { eventType = "Profile Switch", profile, duration, created_at = At(mills), enteredBy = "test" });

    private Task PostEventAsync(string eventType, long mills) =>
        PostAsync(new { eventType, created_at = At(mills), enteredBy = "test" });

    private Task<Nocturne.Core.Models.Treatment?> LatestAsync(string eventType, long at, long window) =>
        WithTenantScopeAsync(sp => sp.GetRequiredService<ITreatmentService>()
            .GetLatestTreatmentOfTypeAsync(eventType, at - window, at));

    [Fact]
    public async Task LatestProfileSwitch_IsTheNewestOneOfZeroDuration()
    {
        var at = Anchor(2015);
        await PostSwitchAsync(at - Day, "Timed", duration: 60);
        await PostSwitchAsync(at - 10 * Day, "Open", duration: 0);
        await PostSwitchAsync(at - 20 * Day, "Older", duration: 0);

        var latest = await LatestAsync("Profile Switch", at, ProfileSwitchWindow);

        latest!.Profile.Should().Be("Open");
    }

    [Fact]
    public async Task LatestProfileSwitch_IsServedFromTheLastThreeHundredAndSeventyTwoDays()
    {
        var inside = Anchor(2017);
        await PostSwitchAsync(inside - ProfileSwitchWindow + Minute, "Inside", duration: 0);
        var outside = Anchor(2019);
        await PostSwitchAsync(outside - ProfileSwitchWindow - Minute, "Outside", duration: 0);

        (await LatestAsync("Profile Switch", inside, ProfileSwitchWindow))!.Profile.Should().Be("Inside");
        (await LatestAsync("Profile Switch", outside, ProfileSwitchWindow)).Should().BeNull();
    }

    [Fact]
    public async Task LatestDeviceEvent_IsServedFromTheLastSixtyTwoDays()
    {
        var at = Anchor(2021);
        await PostEventAsync("Insulin Change", at - EventWindow + Minute);
        await PostEventAsync("Pump Battery Change", at - EventWindow - Minute);

        var inside = await LatestAsync("Insulin Change", at, EventWindow);
        var outside = await LatestAsync("Pump Battery Change", at, EventWindow);

        inside!.EventType.Should().Be("Insulin Change");
        inside.Mills.Should().Be(at - EventWindow + Minute);
        outside.Should().BeNull();
    }

    [Fact]
    public async Task LatestDeviceEvent_IsTheNewestAtOrBeforeTheTime()
    {
        var at = Anchor(2023);
        await PostEventAsync("Site Change", at - 3 * Day);
        await PostEventAsync("Site Change", at - Day);
        await PostEventAsync("Site Change", at + Minute);

        var latest = await LatestAsync("Site Change", at, EventWindow);

        latest!.Mills.Should().Be(at - Day);
    }
}
