using Microsoft.Win32;

namespace Nocturne.Desktop.Tray.Helpers;

/// <summary>
/// Manages the Windows "Run" registry key so the tray app can start on login.
/// </summary>
public static class StartupHelper
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NocturneTray";

    /// <summary>
    /// Writes or removes the registry value that launches the tray app on login.
    /// </summary>
    public static void SetStartOnLogin(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return;

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Returns whether the registry value for start-on-login is currently present.
    /// </summary>
    public static bool IsStartOnLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }
}
