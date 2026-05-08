using System.Text.Json;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Upgrades a stored Halo Dial JSON blob to the current
/// <see cref="HaloDialConfig"/> shape. The migration chain is currently a
/// no-op (only v1 exists) but the dispatch is in place so future shape
/// changes can be applied without ad-hoc data migrations.
/// </summary>
public static class HaloDialSchemaMigrator
{
    /// <summary>
    /// Latest <see cref="HaloDialConfig.SchemaVersion"/> understood by this migrator.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Migrate raw JSON to the current schema and deserialize to <see cref="HaloDialConfig"/>.
    /// Empty / non-object inputs and malformed payloads return defaults rather than throwing.
    /// </summary>
    public static HaloDialConfig Migrate(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            return new HaloDialConfig();

        // Future: while (version < CurrentSchemaVersion) { raw = MigrateVN_to_VNplus1(raw); version++; }
        // Read schemaVersion defensively in case future migrations need it.
        _ = raw.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out _);

        try
        {
            return JsonSerializer.Deserialize<HaloDialConfig>(raw.GetRawText(), DeserializeOptions)
                   ?? new HaloDialConfig();
        }
        catch (JsonException)
        {
            return new HaloDialConfig();
        }
    }
}
