using System.Text.Json;
using Nocturne.Desktop.Tray.Models;
using Windows.Security.Credentials;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Persists settings to %APPDATA%/Nocturne/tray-settings.json
/// and stores OIDC tokens securely in Windows Credential Manager.
/// </summary>
public sealed class SettingsService
{
    private const string AppDataFolder = "Nocturne";
    private const string SettingsFileName = "tray-settings.json";
    private const string CredentialResource = "Nocturne.Desktop.Tray";
    private const string AccessTokenKey = "access-token";
    private const string RefreshTokenKey = "refresh-token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _settingsPath;
    private TraySettings _settings = new();

    public TraySettings Settings => _settings;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, AppDataFolder);
        Directory.CreateDirectory(folder);
        _settingsPath = Path.Combine(folder, SettingsFileName);
    }

    public async Task LoadAsync()
    {
        if (File.Exists(_settingsPath))
        {
            var json = await File.ReadAllTextAsync(_settingsPath);
            _settings = JsonSerializer.Deserialize<TraySettings>(json, JsonOptions) ?? new TraySettings();
        }
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    public void StoreSecret(string key, string value)
    {
        var vault = new PasswordVault();

        // Remove existing credential if present
        try
        {
            var existing = vault.Retrieve(CredentialResource, key);
            vault.Remove(existing);
        }
        catch (Exception)
        {
            // Credential doesn't exist yet
        }

        vault.Add(new PasswordCredential(CredentialResource, key, value));
    }

    public string? RetrieveSecret(string key)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void RemoveSecret(string key)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource, key);
            vault.Remove(credential);
        }
        catch (Exception)
        {
            // Credential doesn't exist
        }
    }

    public string? GetAccessToken() => RetrieveSecret(AccessTokenKey);
    public void SetAccessToken(string value) => StoreSecret(AccessTokenKey, value);

    public string? GetRefreshToken() => RetrieveSecret(RefreshTokenKey);
    public void SetRefreshToken(string value) => StoreSecret(RefreshTokenKey, value);

    public void ClearTokens()
    {
        RemoveSecret(AccessTokenKey);
        RemoveSecret(RefreshTokenKey);
    }

    public bool HasServerUrl => !string.IsNullOrWhiteSpace(_settings.ServerUrl);

    public bool IsAuthenticated => GetAccessToken() is not null;
}
