using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure;

/// <summary>
/// JSON-file implementation of <see cref="IWidgetSettingsStore"/>, mirroring the tray app's
/// settings storage. Stored under %APPDATA%\Nocturne (redirected to the package's local store
/// for the packaged widget). Cached per process since each widget activation is short-lived.
/// </summary>
public sealed class WidgetSettingsStore : IWidgetSettingsStore
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nocturne");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "widget-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<WidgetSettingsStore> _logger;
    private WidgetSettings? _cached;

    /// <summary>Initializes the store with a logger.</summary>
    public WidgetSettingsStore(ILogger<WidgetSettingsStore> logger) => _logger = logger;

    /// <inheritdoc />
    public async Task<WidgetSettings> GetAsync()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = await File.ReadAllTextAsync(SettingsPath);
                _cached = JsonSerializer.Deserialize<WidgetSettings>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read widget settings; using defaults");
        }

        return _cached ??= new WidgetSettings();
    }

    /// <inheritdoc />
    public async Task SaveAsync(WidgetSettings settings)
    {
        _cached = settings;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save widget settings");
        }
    }
}
