using Nocturne.API.Services.Platform;
using Nocturne.Core.Models.Demo;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Seeding;

/// <summary>
/// Which tenants <see cref="SampleDataLiveFeedService"/> carries on: the tenants a dev stack seeded when
/// <see cref="SampleDataLiveFeed.EnabledKey"/> is set, and the demo clinic's people when demo mode is on. The demo
/// host is never fed here, since the demo service streams its realtime data.
/// </summary>
/// <param name="SampleTenants">Whether a tenant that is not a demo tenant is fed.</param>
/// <param name="DemoPeople">Whether a demo tenant of kind <see cref="DemoTenantKind.Person"/> is fed.</param>
public sealed record SampleDataFeedScope(bool SampleTenants, bool DemoPeople)
{
    /// <summary>Whether the feed runs at all.</summary>
    public bool Any => SampleTenants || DemoPeople;

    public static SampleDataFeedScope From(IConfiguration configuration) => new(
        configuration.GetValue<bool>(SampleDataLiveFeed.EnabledKey),
        DemoModeService.IsEnabledIn(configuration));

    public bool Admits(TenantRecord tenant) => tenant.IsDemo
        ? DemoPeople && tenant.DemoConfig?.Kind == DemoTenantKind.Person
        : SampleTenants;
}
