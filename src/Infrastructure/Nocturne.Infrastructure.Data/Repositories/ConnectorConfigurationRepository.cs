using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IConnectorConfigurationRepository"/> over <see cref="NocturneDbContext"/>, one context
/// per call, saving each write.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds. Writes are tracked reads and saves, which update
/// only the columns they change, so a health report cannot undo a configuration save. The cursor and
/// low-water-mark writes are the exception on PostgreSQL: each is one jsonb-path statement, so two
/// writers of different keys on the same row cannot drop each other's key, as a read-modify-write of
/// the whole object could. A dropped low-water mark is stranded history the failure marks exist to
/// prevent. Same-key writes stay last-writer-wins.
/// </remarks>
public class ConnectorConfigurationRepository : IConnectorConfigurationRepository
{
    private const string ConnectorSourceSuffix = "-connector";

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly INocturneDbContextFactory _contexts;
    private readonly TimeProvider _time;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public ConnectorConfigurationRepository(INocturneDbContextFactory contexts, TimeProvider? time = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public virtual async Task<ConnectorConfigurationRecord?> FindAsync(string connectorName, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var name = ConnectorNames.Canonical(connectorName);
        var entity = await db.ConnectorConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectorName == name, ct);
        return entity is null ? null : ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<ConnectorConfigurationRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await ListAsync(db, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<ConnectorConfigurationRecord>> ListForTenantAsync(
        Guid tenantId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateForTenantAsync(tenantId, ct);
        return await ListAsync(db, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await db.ConnectorConfigurations.AnyAsync(ct);
    }

    /// <inheritdoc/>
    public virtual Task<ConnectorConfigurationRecord> SaveConfigurationAsync(
        string connectorName, string configurationJson, string? modifiedBy, CancellationToken ct) =>
        UpsertAsync(connectorName, modifiedBy, entity => entity.ConfigurationJson = configurationJson, ct);

    /// <inheritdoc/>
    public virtual Task<ConnectorConfigurationRecord> SaveSecretsAsync(
        string connectorName, string secretsJson, string? modifiedBy, CancellationToken ct) =>
        UpsertAsync(connectorName, modifiedBy, entity => entity.SecretsJson = secretsJson, ct);

    /// <inheritdoc/>
    public virtual Task<ConnectorConfigurationRecord> SetActiveAsync(
        string connectorName, bool isActive, string? modifiedBy, CancellationToken ct) =>
        UpsertAsync(connectorName, modifiedBy,
            entity => entity.ConfigurationJson = WithEnabled(entity.ConfigurationJson, isActive), ct);

    /// <inheritdoc/>
    public virtual async Task<ConnectorConfigurationRecord?> DeleteAsync(string connectorName, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await FindTrackedAsync(db, connectorName, ct);
        if (entity is null)
            return null;

        db.ConnectorConfigurations.Remove(entity);
        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<ConnectorConfigurationRecord?> RecordHealthAsync(
        string connectorName, ConnectorHealthUpdate update, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await FindTrackedAsync(db, connectorName, ct);
        if (entity is null)
            return null;

        if (update.LastSyncAttempt is { } attempt)
            entity.LastSyncAttempt = attempt;
        if (update.LastSuccessfulSync is { } success)
            entity.LastSuccessfulSync = success;
        if (update.ClearLastErrorMessage)
            entity.LastErrorMessage = null;
        else if (update.LastErrorMessage is { } message)
            entity.LastErrorMessage = FitErrorMessage(message);
        if (update.ClearLastErrorAt)
            entity.LastErrorAt = null;
        else if (update.LastErrorAt is { } errorAt)
            entity.LastErrorAt = errorAt;
        if (update.IsHealthy is { } healthy)
            entity.IsHealthy = healthy;

        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<JsonNode?> GetSyncCursorAsync(string connectorName, string resource, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var name = ConnectorNames.Canonical(connectorName);
        var json = await db.ConnectorConfigurations
            .Where(c => c.ConnectorName == name)
            .Select(c => c.SyncCursorsJson)
            .FirstOrDefaultAsync(ct);

        return ReadObject(json)[resource]?.DeepClone();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetSyncCursorAsync(
        string connectorName, string resource, JsonNode value, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var name = ConnectorNames.Canonical(connectorName);
        if (db.Database.IsNpgsql())
        {
            var json = value.ToJsonString();
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations
                SET sync_cursors = jsonb_set(CASE WHEN jsonb_typeof(sync_cursors) = 'object'
                    THEN sync_cursors ELSE jsonb_build_object() END, ARRAY[{resource}], {json}::jsonb),
                    sys_updated_at = {_time.GetUtcNow().UtcDateTime}
                WHERE tenant_id = {db.TenantId} AND connector_name = {name}
                """, ct) > 0;
        }

        return await UpdateCursorsAsync(db, name, cursors => cursors[resource] = value.DeepClone(), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RemoveSyncCursorAsync(string connectorName, string resource, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var name = ConnectorNames.Canonical(connectorName);
        if (db.Database.IsNpgsql())
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE connector_configurations
                SET sync_cursors = (CASE WHEN jsonb_typeof(sync_cursors) = 'object'
                    THEN sync_cursors ELSE jsonb_build_object() END) - {resource},
                    sys_updated_at = {_time.GetUtcNow().UtcDateTime}
                WHERE tenant_id = {db.TenantId} AND connector_name = {name}
                """, ct) > 0;
        }

        return await UpdateCursorsAsync(db, name, cursors => cursors.Remove(resource), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<DateTime?> GetBackfillLowWaterMarkAsync(
        string source, string collection, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var config = await FindBySourceAsync(db, source, tracked: false, ct);
        if (config?.BackfillLowWaterMarks is null)
            return null;

        var marks = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(config.BackfillLowWaterMarks);
        return marks is not null && marks.TryGetValue(collection, out var mark)
            ? DateTime.SpecifyKind(mark, DateTimeKind.Utc)
            : null;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetBackfillLowWaterMarkAsync(
        string source, string collection, DateTime? lowWaterMark, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var isNpgsql = db.Database.IsNpgsql();
        var config = await FindBySourceAsync(db, source, tracked: !isNpgsql, ct);
        if (config is null)
            return false;

        if (isNpgsql)
        {
            if (lowWaterMark is null)
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE connector_configurations
                     SET backfill_low_water_marks =
                         NULLIF(coalesce(backfill_low_water_marks, jsonb_build_object()) - {collection}, jsonb_build_object())
                     WHERE id = {config.Id}
                     """,
                    ct);
            }
            else
            {
                // ISO-8601 UTC ("...Z"), as System.Text.Json writes it, so the read round-trips
                // without a timezone shift.
                var value = lowWaterMark.Value.ToString("O");
                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE connector_configurations
                     SET backfill_low_water_marks =
                         jsonb_set(coalesce(backfill_low_water_marks, jsonb_build_object()), ARRAY[{collection}], to_jsonb({value}::text))
                     WHERE id = {config.Id}
                     """,
                    ct);
            }

            return true;
        }

        var marks = config.BackfillLowWaterMarks is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(config.BackfillLowWaterMarks) ?? [];

        if (lowWaterMark is null)
            marks.Remove(collection);
        else
            marks[collection] = lowWaterMark.Value;

        config.BackfillLowWaterMarks = marks.Count == 0 ? null : JsonSerializer.Serialize(marks);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ConnectorConfigurationRecord> UpsertAsync(
        string connectorName,
        string? modifiedBy,
        Action<ConnectorConfigurationEntity> change,
        CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await FindTrackedAsync(db, connectorName, ct);
        if (entity is null)
        {
            entity = new ConnectorConfigurationEntity
            {
                ConnectorName = connectorName,
                ConfigurationJson = "{}",
                SecretsJson = "{}",
                ModifiedBy = modifiedBy,
            };
            change(entity);
            db.ConnectorConfigurations.Add(entity);
        }
        else
        {
            change(entity);
            entity.LastModified = _time.GetUtcNow();
            entity.ModifiedBy = modifiedBy;
        }

        await db.SaveChangesAsync(ct);
        return ToRecord(entity);
    }

    private async Task<bool> UpdateCursorsAsync(
        NocturneDbContext db, string connectorName, Action<JsonObject> update, CancellationToken ct)
    {
        var entity = await FindTrackedAsync(db, connectorName, ct);
        if (entity is null)
            return false;

        var cursors = ReadObject(entity.SyncCursorsJson);
        update(cursors);
        entity.SyncCursorsJson = cursors.ToJsonString();

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static Task<ConnectorConfigurationEntity?> FindTrackedAsync(
        NocturneDbContext db, string connectorName, CancellationToken ct)
    {
        var name = ConnectorNames.Canonical(connectorName);
        return db.ConnectorConfigurations.FirstOrDefaultAsync(c => c.ConnectorName == name, ct);
    }

    /// <summary>
    /// Sources are named <c>{connector}-connector</c> while rows carry the bare connector name, so
    /// the suffix is stripped, with an exact match as fallback.
    /// </summary>
    private static Task<ConnectorConfigurationEntity?> FindBySourceAsync(
        NocturneDbContext db, string source, bool tracked, CancellationToken ct)
    {
        var canonicalSource = ConnectorNames.Canonical(source);
        var name = canonicalSource.EndsWith(ConnectorSourceSuffix, StringComparison.Ordinal)
            ? canonicalSource[..^ConnectorSourceSuffix.Length]
            : canonicalSource;

        var query = tracked ? db.ConnectorConfigurations : db.ConnectorConfigurations.AsNoTracking();
        return query.FirstOrDefaultAsync(c => c.ConnectorName == name || c.ConnectorName == canonicalSource, ct);
    }

    private static async Task<IReadOnlyList<ConnectorConfigurationRecord>> ListAsync(
        NocturneDbContext db, CancellationToken ct)
    {
        var entities = await db.ConnectorConfigurations.AsNoTracking()
            .OrderBy(c => c.ConnectorName)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    private static JsonObject ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            // Shared keys include progress snapshots, which are not cursors.
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A corrupt blob reads as empty, so it can never wedge a sync.
            return new JsonObject();
        }
    }

    private static string WithEnabled(string configurationJson, bool enabled)
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configurationJson, CamelCase)
            ?? new Dictionary<string, JsonElement>();

        if (config.Keys.FirstOrDefault(k => k.Equals("enabled", StringComparison.OrdinalIgnoreCase)) is { } existing)
            config.Remove(existing);

        using var doc = JsonDocument.Parse(enabled ? "true" : "false");
        config["enabled"] = doc.RootElement.Clone();

        return JsonSerializer.Serialize(config, CamelCase);
    }

    private static string FitErrorMessage(string message)
    {
        const string marker = "... (truncated)";
        const int max = ConnectorConfigurationRecord.LastErrorMessageMaxLength;

        if (message.Length <= max)
            return message;

        var cut = max - marker.Length;
        if (char.IsHighSurrogate(message[cut - 1]))
            cut--;

        return string.Concat(message.AsSpan(0, cut), marker);
    }

    private static ConnectorConfigurationRecord ToRecord(ConnectorConfigurationEntity entity) => new(
        entity.TenantId,
        entity.ConnectorName,
        entity.ConfigurationJson,
        entity.SecretsJson,
        entity.SchemaVersion,
        entity.LastModified,
        entity.ModifiedBy,
        entity.LastSyncAttempt,
        entity.LastSuccessfulSync,
        entity.LastErrorMessage,
        entity.LastErrorAt,
        entity.IsHealthy,
        entity.SyncCursorsJson,
        entity.BackfillLowWaterMarks);
}
