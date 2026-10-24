namespace Nocturne.Core.Constants;

/// <summary>Keys of rows in the settings table that are not Nightscout v3 settings documents.</summary>
public static class SettingsKeys
{
    /// <summary>
    /// Prefix of the UI-settings rows (<c>UISettingsService</c>). The v3 settings collection
    /// neither serves nor writes them: their sections have their own read and write scopes.
    /// </summary>
    public const string UiSettingsPrefix = "ui:settings:";

    /// <summary>Whether <paramref name="key"/> names a UI-settings row.</summary>
    public static bool IsUiSettings(string? key) =>
        key is not null && key.StartsWith(UiSettingsPrefix, StringComparison.Ordinal);
}
