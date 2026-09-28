using System.Text.Json;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Reads glucose processing preferences and source-default rules from the settings repository.
/// </summary>
/// <remarks>
/// Scoped, and each value is read once per scope and tenant: the resolver asks for them once per
/// reading, so a batch of readings would otherwise re-read the same two settings rows for every
/// reading in it. Keyed by tenant because a scope can switch tenants, as DevAdmin's sync-all does.
/// </remarks>
/// <seealso cref="IGlucoseProcessingConfigProvider"/>
/// <seealso cref="GlucoseProcessingResolver"/>
public class GlucoseProcessingConfigProvider(ISettingsRepository settingsRepository, ITenantAccessor tenantAccessor)
    : IGlucoseProcessingConfigProvider
{
    private const string PreferenceKey = "preferredGlucoseProcessing";
    private const string SourceDefaultsKey = "glucoseProcessingSourceDefaults";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Dictionary<Guid, GlucoseProcessing?> _preferred = [];
    private readonly Dictionary<Guid, List<GlucoseProcessingSourceDefault>> _sourceDefaults = [];

    public async Task<GlucoseProcessing?> GetPreferredProcessingAsync(CancellationToken ct = default)
    {
        if (_preferred.TryGetValue(tenantAccessor.TenantId, out var cached))
            return cached;

        var settings = await settingsRepository.GetSettingsByKeyAsync(PreferenceKey, ct);
        var raw = settings?.Value?.ToString()?.Trim('"');
        GlucoseProcessing? value = Enum.TryParse<GlucoseProcessing>(raw, ignoreCase: true, out var gp) ? gp : null;
        _preferred[tenantAccessor.TenantId] = value;
        return value;
    }

    public async Task<List<GlucoseProcessingSourceDefault>> GetSourceDefaultsAsync(CancellationToken ct = default)
    {
        if (_sourceDefaults.TryGetValue(tenantAccessor.TenantId, out var cached))
            return [.. cached];

        var settings = await settingsRepository.GetSettingsByKeyAsync(SourceDefaultsKey, ct);
        var json = settings?.Value is JsonElement element
            ? element.GetRawText()
            : settings?.Value?.ToString();

        var defaults = string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<GlucoseProcessingSourceDefault>>(json, JsonOptions) ?? [];
        _sourceDefaults[tenantAccessor.TenantId] = defaults;
        return [.. defaults];
    }

    public async Task SetPreferredProcessingAsync(GlucoseProcessing? processing, CancellationToken ct = default)
    {
        var existing = await settingsRepository.GetSettingsByKeyAsync(PreferenceKey, ct);
        var value = processing?.ToString();

        if (existing is not null)
        {
            existing.Value = value;
            await settingsRepository.UpdateSettingsAsync(existing.Id!, existing, ct);
        }
        else if (value is not null)
        {
            await settingsRepository.CreateSettingsAsync(
            [
                new Settings { Key = PreferenceKey, Value = value, IsActive = true }
            ], ct);
        }

        _preferred[tenantAccessor.TenantId] = processing;
    }

    public async Task SetSourceDefaultsAsync(List<GlucoseProcessingSourceDefault> defaults, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(defaults, JsonOptions);
        var existing = await settingsRepository.GetSettingsByKeyAsync(SourceDefaultsKey, ct);

        if (existing is not null)
        {
            existing.Value = json;
            await settingsRepository.UpdateSettingsAsync(existing.Id!, existing, ct);
        }
        else
        {
            await settingsRepository.CreateSettingsAsync(
            [
                new Settings { Key = SourceDefaultsKey, Value = json, IsActive = true }
            ], ct);
        }

        _sourceDefaults[tenantAccessor.TenantId] = [.. defaults];
    }
}
