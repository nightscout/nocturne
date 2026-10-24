namespace Nocturne.Core.Constants;

/// <summary>Keys of rows in the settings table that are not Nightscout v3 settings documents.</summary>
public static class SettingsKeys
{
    /// <summary>
    /// Prefix of the UI-settings rows (<c>UISettingsService</c>), whose sections have their own read
    /// and write scopes.
    /// </summary>
    public const string UiSettingsPrefix = "ui:settings:";

    /// <summary>Prefix of the rows connector services keep (<c>tenant.settings</c> scope).</summary>
    public const string ConnectorsPrefix = "connectors:";

    /// <summary>
    /// Whether <paramref name="key"/> names a row Nocturne owns, which the v3 settings collection
    /// neither serves nor writes.
    /// </summary>
    public static bool IsReserved(string? key) =>
        key is not null
        && (key.StartsWith(UiSettingsPrefix, StringComparison.Ordinal)
            || key.StartsWith(ConnectorsPrefix, StringComparison.Ordinal));
}
