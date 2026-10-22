namespace Nocturne.API.Services.Caching;

/// <summary>
/// The <see cref="ITenantCache"/> key families held in the distributed tier: the first segment of
/// every key a cache writes there, and what <see cref="ITenantCache.RemoveFamilyAsync"/> clears.
/// </summary>
public static class TenantCacheFamily
{
    /// <summary>The legacy entry reads, built from sensor glucose, meter glucose and calibrations.</summary>
    public const string Entries = "entries";

    /// <summary>The legacy treatment reads, built from the V4 treatment-like records.</summary>
    public const string Treatments = "treatments";

    /// <summary>The multi-period statistics.</summary>
    public const string Statistics = "statistics";

    /// <summary>
    /// The eHbA1c timeline, keyed by year below this segment; see
    /// <see cref="Analytics.DataOverviewService.EHbA1cCacheFamilies"/>.
    /// </summary>
    public const string EHbA1c = "ehba1c";

    /// <summary>The profile activations the profile resolvers read.</summary>
    public const string ProfileActivations = "profile-activations";
}
