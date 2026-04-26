using System.Text.Json;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Reads glucose processing preferences and source-default rules from the settings repository.
/// </summary>
/// <seealso cref="IGlucoseProcessingConfigProvider"/>
/// <seealso cref="GlucoseProcessingResolver"/>
public class GlucoseProcessingConfigProvider(ISettingsRepository settingsRepository) : IGlucoseProcessingConfigProvider
{
    private const string PreferenceKey = "preferredGlucoseProcessing";
    private const string SourceDefaultsKey = "glucoseProcessingSourceDefaults";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<GlucoseProcessing?> GetPreferredProcessingAsync(CancellationToken ct = default)
    {
        var settings = await settingsRepository.GetSettingsByKeyAsync(PreferenceKey, ct);
        if (settings?.Value is null)
            return null;

        var raw = settings.Value.ToString()?.Trim('"');
        return Enum.TryParse<GlucoseProcessing>(raw, ignoreCase: true, out var gp) ? gp : null;
    }

    public async Task<List<GlucoseProcessingSourceDefault>> GetSourceDefaultsAsync(CancellationToken ct = default)
    {
        var settings = await settingsRepository.GetSettingsByKeyAsync(SourceDefaultsKey, ct);
        if (settings?.Value is null)
            return [];

        var json = settings.Value is JsonElement element
            ? element.GetRawText()
            : settings.Value.ToString();

        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<GlucoseProcessingSourceDefault>>(json, JsonOptions) ?? [];
    }
}
