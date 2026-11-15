using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>
/// A public share reads the tenant's zone and timezone timeline past the RLS that hides the rows
/// they are stored in.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class ShareClockIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    /// <remarks>
    /// The patient record and the timeline name different zones, so the device dates can only fall on
    /// Adelaide days if the timeline was read.
    /// </remarks>
    [Theory]
    [InlineData("glucose.read")]
    [InlineData("")]
    public async Task AShare_GetsTheTenantsZoneAndTimeline_ThoughItCannotReadTheirRows(string categories)
    {
        var slug = $"share-clock-{Guid.NewGuid():N}"[..24];
        var tenantId = await AuthTestHelpers.SeedTenantAsync(Fixture, slug, "Share clock");
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(tenantId, slug, "Share clock", true, false));
        await using (var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync())
        {
            context.PatientRecords.Add(new PatientRecordEntity { Id = Guid.NewGuid(), Timezone = "Europe/Stockholm" });
            context.TimezoneTimeline.Add(new TimezoneTimelineEntity
            {
                Id = Guid.NewGuid(),
                EffectiveFrom = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified),
                Timezone = "Australia/Adelaide",
            });
            await context.SaveChangesAsync();
        }

        var read = scope.ServiceProvider.GetRequiredService<ICategoryReadContext>();
        read.MarkShare();
        read.SetVisibleCategories(categories);
        read.SetFullHistory(false);

        (await scope.ServiceProvider.GetRequiredService<ITherapySettingsResolver>().GetTimezoneAsync())
            .Should().BeNull("the share's own reads cannot see the patient record");

        var clock = await scope.ServiceProvider.GetRequiredService<TenantClockResolver>().ResolveAsync(CancellationToken.None);
        clock.Zone!.Id.Should().Be("Europe/Stockholm");
        clock.Reason.Should().BeNull();

        var deviceDates = await scope.ServiceProvider.GetRequiredService<DeviceDateClockResolver>().ResolveAsync(CancellationToken.None);
        deviceDates.LocalDate(new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateOnly(2026, 6, 11), "15:00 UTC is past midnight in Adelaide but not in Stockholm");
    }

    [Theory]
    [InlineData(Scope.GlucoseRead)]
    [InlineData(Scope.HeartRateRead)]
    public async Task AShare_GetsThePatientTimezone_TheReportWindowsAreCutOn(string granted)
    {
        static object[] Values(double value) => [new { time = "00:00", value }];
        var profile = new
        {
            defaultProfile = "Default",
            startDate = "2024-01-01T00:00:00.000Z",
            units = "mg/dl",
            store = new Dictionary<string, object>
            {
                ["Default"] = new
                {
                    dia = 4, timezone = "Australia/Adelaide", units = "mg/dl",
                    basal = Values(1.0), carbratio = Values(10), sens = Values(50),
                    target_low = Values(90), target_high = Values(140),
                },
            },
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/profile", profile)).EnsureSuccessStatusCode();

        using var share = await CreateShareClientAsync([granted]);
        var response = await share.GetAsync("/api/v4/patient-record/timezone");
        var body = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.Should().BeTrue(body);
        JsonDocument.Parse(body).RootElement.GetProperty("timezone").GetString().Should().Be("Australia/Adelaide");
    }

    [Fact]
    public async Task AnAnonymousCallerOnTheTenantHost_IsRefusedThePatientTimezone()
    {
        using var anonymous = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        var response = await anonymous.GetAsync("/api/v4/patient-record/timezone");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpClient> CreateShareClientAsync(string[] scopes)
    {
        using var owner = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId));
        var link = await (await owner.PostAsync("/api/v4/share/rotate", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        (await owner.PutAsJsonAsync("/api/v4/share/scopes", new { scopes })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v4/share/full-history", new { fullHistory = true })).EnsureSuccessStatusCode();

        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Host = new Uri(link.GetProperty("url").GetString()!).Authority;
        return client;
    }
}
