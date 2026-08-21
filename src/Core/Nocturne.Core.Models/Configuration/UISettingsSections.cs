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
    Action<UISettingsConfiguration, object> Set
);

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
            (s, v) => s.Devices = (DeviceSettings)v
        ),
        new(
            "algorithm",
            typeof(AlgorithmSettings),
            s => s.Algorithm,
            (s, v) => s.Algorithm = (AlgorithmSettings)v
        ),
        new(
            "features",
            typeof(FeatureSettings),
            s => s.Features,
            (s, v) => s.Features = (FeatureSettings)v
        ),
        new(
            Notifications,
            typeof(NotificationSettings),
            s => s.Notifications,
            (s, v) => s.Notifications = (NotificationSettings)v
        ),
        new(
            "services",
            typeof(ServicesSettings),
            s => s.Services,
            (s, v) => s.Services = (ServicesSettings)v
        ),
        new(
            "dataQuality",
            typeof(DataQualitySettings),
            s => s.DataQuality,
            (s, v) => s.DataQuality = (DataQualitySettings)v
        ),
        new(
            "security",
            typeof(SecuritySettings),
            s => s.Security,
            (s, v) => s.Security = (SecuritySettings)v
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
