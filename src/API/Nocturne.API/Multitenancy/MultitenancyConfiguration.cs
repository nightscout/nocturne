namespace Nocturne.API.Multitenancy;

/// <summary>
/// Configuration for the multitenancy system.
/// Bound from appsettings.json section "Multitenancy".
/// </summary>
public class MultitenancyConfiguration
{
    public const string SectionName = "Multitenancy";

    /// <summary>
    /// Base domain for subdomain tenant resolution.
    /// e.g. "nocturnecgm.com" — requests to rhys.nocturnecgm.com resolve tenant "rhys".
    /// </summary>
    public string BaseDomain { get; set; } = "";
}
