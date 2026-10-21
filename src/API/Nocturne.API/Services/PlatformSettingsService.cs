using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

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
    private readonly INocturneDbContextFactory _contexts;
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
            // Optional: only needed for the in-app "Link my Discord account"
            // OAuth2 flow. The bot itself works without it (users link via
            // the /connect command). Application ID doubles as the client ID.
            new("clientSecret", "Client Secret", false),
        ],
        ["slack"] =
        [
            new("botToken", "Bot Token", true),
            new("signingSecret", "Signing Secret", true),
        ],
        ["telegram"] =
        [
            new("botToken", "Bot Token", true),
            // Must match the secret_token passed to Telegram's setWebhook.
            new("webhookSecretToken", "Webhook Secret Token", true),
        ],
        ["whatsapp"] =
        [
            new("accessToken", "Access Token", true),
            new("appSecret", "App Secret", true),
            new("phoneNumberId", "Phone Number ID", true),
            new("verifyToken", "Verify Token", true),
        ],
        ["resend"] =
        [
            new("apiKey", "API Key", true),
            new("fromAddress", "From Address", true),
            new("fromName", "From Name", false),
            new("webhookSecret", "Webhook Secret", false),
        ],
    };

    /// <summary>Formats a field's value must match when one is supplied, with the message shown otherwise.</summary>
    private static readonly Dictionary<(string Category, string Field), (Regex Pattern, string Message)> FieldFormats = new()
    {
        // Telegram's setWebhook accepts only this alphabet and length for secret_token.
        [("telegram", "webhookSecretToken")] = (
            new Regex("^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant),
            "Webhook Secret Token must be 1-256 characters of letters, digits, '_' or '-'"),
    };

    public PlatformSettingsService(INocturneDbContextFactory contexts, ISecretEncryptionService encryption)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _encryption = encryption;
    }

    public static IReadOnlyDictionary<string, List<FieldDefinition>> GetSchemas() => CategorySchemas;

    public static bool IsValidCategory(string category) => CategorySchemas.ContainsKey(category);

    private static HashSet<string> GetValidFieldNames(string category)
        => CategorySchemas.TryGetValue(category, out var fields)
            ? fields.Select(f => f.Name).ToHashSet()
            : [];

    /// <summary>Errors for supplied values that do not match their field's format; blank values are skipped.</summary>
    public static Dictionary<string, string> ValidateFormats(string category, IReadOnlyDictionary<string, string> fields)
    {
        var errors = new Dictionary<string, string>();
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (FieldFormats.TryGetValue((category, key), out var format) && !format.Pattern.IsMatch(value))
                errors[key] = format.Message;
        }
        return errors;
    }

    public async Task<List<PlatformSettingsSummary>> GetAllAsync()
    {
        await using var db = await _contexts.CreateAsync();
        var entities = await db.PlatformSettings.AsNoTracking().ToListAsync();
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
        await using var db = await _contexts.CreateAsync();
        if (!IsValidCategory(category)) return null;
        var entity = await db.PlatformSettings.AsNoTracking()
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
        await using var db = await _contexts.CreateAsync();
        if (!_encryption.IsConfigured)
            return [];

        var entities = await db.PlatformSettings.AsNoTracking().ToListAsync();
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
        await using var db = await _contexts.CreateAsync();
        if (!IsValidCategory(category))
            return (false, new() { ["category"] = "Unknown category" });

        if (!_encryption.IsConfigured)
            return (false, new() { ["_"] = "Instance encryption key is not configured. Set the instance key before saving credentials." });

        var schema = CategorySchemas[category];
        var validFieldNames = GetValidFieldNames(category);

        var entity = await db.PlatformSettings
            .FirstOrDefaultAsync(e => e.Category == category);

        // Merge: non-empty incoming fields overwrite, empty fields preserve existing
        var existing = new Dictionary<string, string>();
        if (entity is not null && entity.EncryptedJson != "{}")
        {
            var enc = JsonSerializer.Deserialize<Dictionary<string, string>>(
                entity.EncryptedJson, JsonOptions) ?? [];
            existing = _encryption.DecryptSecrets(enc);
        }

        var formatErrors = ValidateFormats(category, fields);
        if (formatErrors.Count > 0)
            return (false, formatErrors);

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
            db.PlatformSettings.Add(entity);
        }
        else
        {
            entity.Enabled = enabled;
            entity.EncryptedJson = encryptedJson;
            entity.ConfiguredFields = configuredFields;
        }

        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<bool> DeleteAsync(string category)
    {
        await using var db = await _contexts.CreateAsync();
        if (!IsValidCategory(category))
            return false;

        var entity = await db.PlatformSettings
            .FirstOrDefaultAsync(e => e.Category == category);

        if (entity is null)
            return false;

        db.PlatformSettings.Remove(entity);
        await db.SaveChangesAsync();
        return true;
    }
}
