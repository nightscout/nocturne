using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// <c>PATCH /api/v4/ui-settings/{section}</c> through the whole pipeline: the section type binds
/// the body, and the merge is taken from the buffered raw JSON.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class UISettingsPatchEndpointTests : ApiIntegrationTestBase
{
    public UISettingsPatchEndpointTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        await db.Settings.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Patches_OfDifferentFieldsSentTogether_BothLand()
    {
        var responses = await Task.WhenAll(
            Patch("features", """{"display":{"focusHours":6}}"""),
            Patch("features", """{"trackerPills":{"enabled":false}}"""),
            Patch("features", """{"display":{"nightMode":true}}"""));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        var stored = await AuthenticatedClient.GetFromJsonAsync<FeatureSettings>("/api/v4/ui-settings/features");
        stored!.Display.FocusHours.Should().Be(6);
        stored.TrackerPills.Enabled.Should().BeFalse();
        stored.Display.NightMode.Should().BeTrue();
    }

    [Fact]
    public async Task APatch_ReturnsTheMergedSection_AndAnAbsentKeyIsNotResetToItsDefault()
    {
        await Patch("dataQuality", """{"sleepSchedule":{"bedtimeHour":1}}""");

        var response = await Patch("dataQuality", """{"compressionLowDetection":{"enabled":false}}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged = await response.Content.ReadFromJsonAsync<DataQualitySettings>();
        merged!.SleepSchedule.BedtimeHour.Should().Be(1);
        merged.CompressionLowDetection.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"display":{"focusHours":"soon"}}""")]
    [InlineData("[1]")]
    [InlineData("""{"Display":{"FocusHours":6}}""")]
    [InlineData("""{"display":{"FocusHours":6}}""")]
    [InlineData("""{"display":{"noSuchField":6}}""")]
    [InlineData("""{"noSuchSection":{}}""")]
    public async Task APatchThatDoesNotFitTheSection_IsRefused(string body)
    {
        var response = await Patch("features", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task APatchKeyThatIsNotAPropertyName_ChangesNothing()
    {
        await Patch("features", """{"display":{"focusHours":6}}""");

        await Patch("features", """{"Display":{"FocusHours":9}}""");

        var stored = await AuthenticatedClient.GetFromJsonAsync<FeatureSettings>("/api/v4/ui-settings/features");
        stored!.Display.FocusHours.Should().Be(6);
    }

    private Task<HttpResponseMessage> Patch(string section, string body) =>
        AuthenticatedClient.PatchAsync(
            $"/api/v4/ui-settings/{section}",
            new StringContent(body, Encoding.UTF8, "application/json"));
}
