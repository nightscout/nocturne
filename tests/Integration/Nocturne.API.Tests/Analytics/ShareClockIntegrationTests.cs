using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
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
            context.PatientRecords.Add(new PatientRecordEntity { Id = Guid.NewGuid(), Timezone = "Australia/Adelaide" });
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
        clock.Zone!.Id.Should().Be("Australia/Adelaide");
        clock.Reason.Should().BeNull();

        var deviceDates = await scope.ServiceProvider.GetRequiredService<DeviceDateClockResolver>().ResolveAsync(CancellationToken.None);
        deviceDates.LocalDate(new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc)).Should().Be(new DateOnly(2026, 6, 11));
    }
}
