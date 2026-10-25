using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// One addressable section of <see cref="UISettingsConfiguration"/>: the name it is known by in API
/// routes and in persisted setting keys, its type, and how to read and write it on the aggregate.
/// </summary>
/// <seealso cref="UISettingsSections"/>
public sealed record UISettingsSection(
    string Name,
    Type Type,
    Func<UISettingsConfiguration, object?> Get,
    Action<UISettingsConfiguration, object?> Set
)
{
    /// <summary>
    /// JSON property names the section's own row must not carry, either because another row owns
    /// the value or because it is not tenant state at all. A client that sends one back cannot
    /// make it stick.
    /// </summary>
    public IReadOnlyList<string> OmittedProperties { get; init; } = [];

    /// <summary>
    /// The scope a caller needs to read the section, or null for presentation config every member
    /// renders with. Set on a section that reveals a data category.
    /// </summary>
    public string? ReadScope { get; init; }

    /// <summary>
    /// Whether the section is withheld from a viewer without an account (a guest or share link)
    /// whatever its scopes: it holds household contact details or delivery credentials.
    /// </summary>
    public bool MemberOnly { get; init; }
}

/// <summary>
/// The sections of <see cref="UISettingsConfiguration"/>. Routing, persistence and section lookup all
/// read this one list, so a section is addressable everywhere or nowhere.
/// </summary>
public static class UISettingsSections
{
    /// <summary>
    /// Name of the section carrying <see cref="NotificationSettings"/>.
    /// </summary>
    public const string Notifications = "notifications";

    /// <summary>
    /// Every section, in the order the aggregate declares them.
    /// </summary>
    public static IReadOnlyList<UISettingsSection> All { get; } =
    [
        new(
            "devices",
            typeof(DeviceSettings),
            s => s.Devices,
            (s, v) => s.Devices = (DeviceSettings?)v
        )
        {
            // Connected devices carry serial numbers and battery state.
            ReadScope = Scope.DevicesRead,
        },
        new(
            "algorithm",
            typeof(AlgorithmSettings),
            s => s.Algorithm,
            (s, v) => s.Algorithm = (AlgorithmSettings)v!
        ),
        new(
            "features",
            typeof(FeatureSettings),
            s => s.Features,
            (s, v) => s.Features = (FeatureSettings)v!
        ),
        new(
            Notifications,
            typeof(NotificationSettings),
            s => s.Notifications,
            (s, v) => s.Notifications = (NotificationSettings?)v
        )
        {
            OmittedProperties = ["alarmConfiguration"],
            // Alarm thresholds and profiles decide when a low alert fires.
            ReadScope = Scope.AlertsRead,
            // The alarm configuration carries emergency contacts and push-delivery keys, and a
            // guest link holds alerts.read by default.
            MemberOnly = true,
        },
        new(
            "services",
            typeof(ServicesSettings),
            s => s.Services,
            (s, v) => s.Services = (ServicesSettings?)v
        )
        {
            OmittedProperties = ["availableServices"],
            // The tenant's connector configuration, which only tenant administration reads.
            ReadScope = Scope.TenantSettings,
        },
        new(
            "dataQuality",
            typeof(DataQualitySettings),
            s => s.DataQuality,
            (s, v) => s.DataQuality = (DataQualitySettings)v!
        ),
    ];

    /// <summary>
    /// The section addressed by <paramref name="name"/>, or null when no section owns that name.
    /// </summary>
    public static UISettingsSection? Find(string name)
    {
        return All.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
        );
    }
}
