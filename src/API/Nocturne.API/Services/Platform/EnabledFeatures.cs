using Nocturne.Core.Constants;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// The instance's Nightscout <c>ENABLE</c> list (<c>Features:Enable</c>, space separated), which
/// <c>settings.enable</c> in the status payload reports.
/// </summary>
public static class EnabledFeatures
{
    /// <summary>Opens the legacy API to any origin. See <see cref="Multitenancy.LegacyApiCorsPolicy"/>.</summary>
    public const string Cors = "cors";

    private static readonly string[] Defaults =
    [
        "careportal", "basal", "iob", "cob", "bwp", "cage", "sage", "iage", "bage",
        "pump", "openaps", "loop", "treatmentnotify", "bgnow", "delta", "direction",
        "timeago", "devicestatus", "upbat", "errorcodes", "profile", "bolus", "dbsize",
        "runtimestate", "ar2",
    ];

    public static string[] Read(IConfiguration configuration)
    {
        var enabledFeatures = configuration[ServiceNames.ConfigKeys.FeaturesEnable];
        return string.IsNullOrEmpty(enabledFeatures)
            ? [.. Defaults]
            : enabledFeatures.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool IsEnabled(IConfiguration configuration, string feature) =>
        Read(configuration).Contains(feature, StringComparer.Ordinal);
}
