using System.Text.Json;
using Nocturne.Desktop.Tray.Models;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Manages persistent application settings stored as JSON.
/// Token storage is handled by ICredentialStore from Widget.Contracts.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nocturne");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "tray-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public TraySettings Settings { get; private set; } = new();

    public bool HasServerUrl =>
        !string.IsNullOrWhiteSpace(Settings.ServerUrl);

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = await File.ReadAllTextAsync(SettingsPath);
                Settings = JsonSerializer.Deserialize<TraySettings>(json, JsonOptions) ?? new TraySettings();
            }
        }
        catch
        {
            Settings = new TraySettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);
        }
        catch
        {
            // Settings save failure is non-fatal
        }
    }
}
