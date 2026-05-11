using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services;

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
    /// Secrets are never exposed in API responses — only via this internal method.
    /// </summary>
    public async Task<List<PlatformCredentials>> GetAllDecryptedAsync()
    {
        var entities = await _db.PlatformSettings.AsNoTracking().ToListAsync();
        var results = new List<PlatformCredentials>();
        foreach (var entity in entities)
        {
            var decrypted = new Dictionary<string, string>();
            if (entity.EncryptedJson != "{}" && _encryption.IsConfigured)
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

        var schema = CategorySchemas[category];

        var entity = await _db.PlatformSettings
            .FirstOrDefaultAsync(e => e.Category == category);

        // Merge: non-empty incoming fields overwrite, empty fields preserve existing
        var existing = new Dictionary<string, string>();
        if (entity is not null && entity.EncryptedJson != "{}" && _encryption.IsConfigured)
        {
            var enc = JsonSerializer.Deserialize<Dictionary<string, string>>(
                entity.EncryptedJson, JsonOptions) ?? [];
            existing = _encryption.DecryptSecrets(enc);
        }

        var merged = new Dictionary<string, string>(existing);
        foreach (var (key, value) in fields)
        {
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
        var encrypted = _encryption.IsConfigured
            ? _encryption.EncryptSecrets(merged)
            : merged;
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
            entity.SysUpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return (true, null);
    }

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
}
