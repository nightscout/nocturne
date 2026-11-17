using System.Text.Json;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Manages MyFitnessPal food-matching settings stored as a JSON blob under the key
/// <c>connectors:myfitnesspal:matching-settings</c> in the tenant settings table.
/// </summary>
/// <seealso cref="IMyFitnessPalMatchingSettingsService"/>
public class MyFitnessPalMatchingSettingsService : IMyFitnessPalMatchingSettingsService
{
    private const string SettingsKey = SettingsKeys.ConnectorsPrefix + "myfitnesspal:matching-settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ISettingsRepository _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<MyFitnessPalMatchingSettingsService> _logger;

    public MyFitnessPalMatchingSettingsService(
        ISettingsRepository settings,
        TimeProvider timeProvider,
        ILogger<MyFitnessPalMatchingSettingsService> logger
    )
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MyFitnessPalMatchingSettings?> GetSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var value = await _settings.GetConnectorSettingAsync(SettingsKey, cancellationToken);

            if (value != null)
            {
                var settings = JsonSerializer.Deserialize<MyFitnessPalMatchingSettings>(
                    value,
                    JsonOptions
                );

                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving MyFitnessPal matching settings");
            return null;
        }

        return new MyFitnessPalMatchingSettings();
    }

    public async Task<MyFitnessPalMatchingSettings> SaveSettingsAsync(
        MyFitnessPalMatchingSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _settings.SetConnectorSettingAsync(
                SettingsKey,
                JsonSerializer.Serialize(settings, JsonOptions),
                "MyFitnessPal matching settings",
                _time.GetUtcNow(),
                cancellationToken
            );
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving MyFitnessPal matching settings");
            throw;
        }
    }
}
