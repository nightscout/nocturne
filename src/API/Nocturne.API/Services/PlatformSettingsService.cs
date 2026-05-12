using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services;

public record FieldDefinition(string Name, string Label, bool Required);

public class PlatformSettingsSummary
{
    public string Category { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public List<string> ConfiguredFields { get; init; } = [];
    public List<FieldDefinition> Fields { get; init; } = [];
}

public class PlatformCredentials
{
    public string Category { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public Dictionary<string, string> Fields { get; init; } = new();
}

public class PlatformSettingsService
{
    private readonly NocturneDbContext _db;
    private readonly ISecretEncryptionService _encryption;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Per-category field definitions. Each field is (name, label, required).
    /// </summary>
    private static readonly Dictionary<string, List<FieldDefinition>> CategorySchemas = new()
    {
        ["discord"] =
        [
            new("botToken", "Bot Token", true),
            new("publicKey", "Public Key", true),
            new("applicationId", "Application ID", true),
        ],
        ["slack"] =
        [
            new("botToken", "Bot Token", true),
            new("signingSecret", "Signing Secret", true),
        ],
        ["telegram"] =
        [
            new("botToken", "Bot Token", true),
        ],
        ["whatsapp"] =
        [
            new("accessToken", "Access Token", true),
            new("appSecret", "App Secret", true),
            new("phoneNumberId", "Phone Number ID", true),
            new("verifyToken", "Verify Token", true),
        ],
    };

    public PlatformSettingsService(NocturneDbContext db, ISecretEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public static IReadOnlyDictionary<string, List<FieldDefinition>> GetSchemas() => CategorySchemas;

    public static bool IsValidCategory(string category) => CategorySchemas.ContainsKey(category);

    private static HashSet<string> GetValidFieldNames(string category)
        => CategorySchemas.TryGetValue(category, out var fields)
            ? fields.Select(f => f.Name).ToHashSet()
            : [];

    public async Task<List<PlatformSettingsSummary>> GetAllAsync()
    {
        var entities = await _db.PlatformSettings.AsNoTracking().ToListAsync();
        return CategorySchemas.Keys.Select(category =>
        {
            var entity = entities.FirstOrDefault(e => e.Category == category);
            return new PlatformSettingsSummary
            {
                Category = category,
                Enabled = entity?.Enabled ?? false,
                ConfiguredFields = entity?.ConfiguredFields ?? [],
                Fields = CategorySchemas[category],
            };
        }).ToList();
    }

    public async Task<PlatformSettingsSummary?> GetAsync(string category)
    {
        if (!IsValidCategory(category)) return null;
        var entity = await _db.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Category == category);
        return new PlatformSettingsSummary
        {
            Category = category,
            Enabled = entity?.Enabled ?? false,
            ConfiguredFields = entity?.ConfiguredFields ?? [],
            Fields = CategorySchemas[category],
        };
    }

    /// <summary>
    /// Returns all decrypted platform credentials for bot initialization.
    /// Only callable via instance-key auth (server-to-server).
    /// </summary>
    public async Task<List<PlatformCredentials>> GetAllDecryptedAsync()
    {
        if (!_encryption.IsConfigured)
            return [];

        var entities = await _db.PlatformSettings.AsNoTracking().ToListAsync();
        var results = new List<PlatformCredentials>();
        foreach (var entity in entities)
        {
            var decrypted = new Dictionary<string, string>();
            if (entity.EncryptedJson != "{}")
            {
                var encrypted = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    entity.EncryptedJson, JsonOptions) ?? [];
                decrypted = _encryption.DecryptSecrets(encrypted);
            }
            results.Add(new PlatformCredentials
            {
                Category = entity.Category,
                Enabled = entity.Enabled,
                Fields = decrypted,
            });
        }
        return results;
    }

    public async Task<(bool Success, Dictionary<string, string>? Errors)> UpsertAsync(
        string category, bool enabled, Dictionary<string, string> fields)
    {
        if (!IsValidCategory(category))
            return (false, new() { ["category"] = "Unknown category" });

        if (!_encryption.IsConfigured)
            return (false, new() { ["_"] = "Instance encryption key is not configured. Set the instance key before saving credentials." });

        var schema = CategorySchemas[category];
        var validFieldNames = GetValidFieldNames(category);

        var entity = await _db.PlatformSettings
            .FirstOrDefaultAsync(e => e.Category == category);

        // Merge: non-empty incoming fields overwrite, empty fields preserve existing
        var existing = new Dictionary<string, string>();
        if (entity is not null && entity.EncryptedJson != "{}")
        {
            var enc = JsonSerializer.Deserialize<Dictionary<string, string>>(
                entity.EncryptedJson, JsonOptions) ?? [];
            existing = _encryption.DecryptSecrets(enc);
        }

        var merged = new Dictionary<string, string>(existing);
        foreach (var (key, value) in fields)
        {
            if (!validFieldNames.Contains(key))
                continue; // Strip unknown keys

            if (!string.IsNullOrWhiteSpace(value))
                merged[key] = value;
        }

        // Validate required fields against merged result when enabling
        var errors = new Dictionary<string, string>();
        if (enabled)
        {
            foreach (var field in schema.Where(f => f.Required))
            {
                if (!merged.TryGetValue(field.Name, out var val) || string.IsNullOrWhiteSpace(val))
                    errors[field.Name] = $"{field.Label} is required";
            }
        }

        if (errors.Count > 0)
            return (false, errors);

        // Encrypt all fields
        var encrypted = _encryption.EncryptSecrets(merged);
        var encryptedJson = JsonSerializer.Serialize(encrypted, JsonOptions);
        var configuredFields = merged
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        if (entity is null)
        {
            entity = new PlatformSettingsEntity
            {
                Category = category,
                Enabled = enabled,
                EncryptedJson = encryptedJson,
                ConfiguredFields = configuredFields,
            };
            _db.PlatformSettings.Add(entity);
        }
        else
        {
            entity.Enabled = enabled;
            entity.EncryptedJson = encryptedJson;
            entity.ConfiguredFields = configuredFields;
        }

        await _db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<bool> DeleteAsync(string category)
    {
        if (!IsValidCategory(category))
            return false;

        var entity = await _db.PlatformSettings
            .FirstOrDefaultAsync(e => e.Category == category);

        if (entity is null)
            return false;

        _db.PlatformSettings.Remove(entity);
        await _db.SaveChangesAsync();
        return true;
    }
}
