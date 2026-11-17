using Microsoft.Extensions.Configuration;

namespace Nocturne.Aspire.Host;

/// <summary>
/// Run-mode dev shortcuts, bound from the AppHost's <c>Dev</c> section (e.g.
/// <c>Dev__AutoLogin=false Dev__AutoSeed__Enabled=false aspire start</c> to reach the first-run
/// wizard and the passkey UI).
/// </summary>
internal sealed record DevOptions
{
    public const string SectionName = "Dev";

    /// <summary>
    /// The web login page signs in as the tenant's first owner through
    /// <c>/api/v4/dev-only/auth/login</c> instead of showing the passkey UI, since a seeded
    /// tenant's passkey is held by no one.
    /// </summary>
    public bool AutoLogin { get; set; } = true;

    public DevAutoSeedOptions AutoSeed { get; set; } = new();

    /// <summary>
    /// The API carries seeded sample data on to the present every minute, so a seeded tenant stays
    /// live (<c>SampleData:Live</c> on the API).
    /// </summary>
    public bool LiveSampleData { get; set; } = true;

    public static DevOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<DevOptions>() ?? new DevOptions();
}

/// <summary>Seeds the <c>dev</c> tenant on an instance that holds no tenant.</summary>
internal sealed record DevAutoSeedOptions
{
    public bool Enabled { get; set; } = true;

    public int SampleDataDays { get; set; } = 30;
}
