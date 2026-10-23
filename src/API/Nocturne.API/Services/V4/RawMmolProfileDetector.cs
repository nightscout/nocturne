using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;

namespace Nocturne.API.Services.V4;

/// <inheritdoc />
/// <remarks>
/// <para>
/// A target range schedule is listed while the migration's closing report would name it: no mg/dL
/// owner without a mmol one, and a low or high target at or under 22.2. An ISF schedule is listed while
/// the migration's <c>MaybeRawIsf</c> holds for it, or its store is a mmol one with an ISF under 9, and
/// its store has no dismiss. The rules are ported rather than shared because no project references the
/// migrations assembly at compile time.
/// </para>
/// <para>
/// The dismiss is the <see cref="IsfIsMgdlKey"/> key on every live therapy settings row of the store. A
/// re-upload writes those rows without it, which is fine: it writes the ISF with targets of its own
/// <c>timestamp</c> in one batch, which <c>MaybeRawIsf</c> already takes as proof.
/// </para>
/// <para>
/// Schedules of profile-switch stores (<see cref="TherapySettings.ProfileSwitchStoreMarker"/>) are left
/// out, as no editor lists them.
/// </para>
/// <para>
/// <c>MaybeRawIsf</c> departs from the migration in one place: a target row written more than the
/// migration's one-minute window after the ISF is not proof of one batch. An upload writes both together,
/// but a hand edit of the targets keeps the store's <c>timestamp</c>, and would otherwise unlist an ISF
/// the owner has not corrected.
/// </para>
/// </remarks>
public sealed class RawMmolProfileDetector(INocturneDbContextFactory contexts) : IRawMmolProfileDetector
{
    /// <summary>The therapy settings <c>additional_properties</c> key of an ISF the owner says is mg/dL.</summary>
    public const string IsfIsMgdlKey = "isfIsMgdl";

    private const string AnyRawTarget =
        "(CASE WHEN jsonb_typeof(e->'Low') = 'number' THEN (e->'Low')::numeric <= 22.2 ELSE false END "
        + "OR CASE WHEN jsonb_typeof(e->'High') = 'number' THEN (e->'High')::numeric <= 22.2 ELSE false END)";

    private const string RawIsf =
        "CASE WHEN jsonb_typeof(e->'Value') = 'number' THEN (e->'Value')::numeric BETWEEN 0.5 AND 22.2 ELSE false END";

    private const string IsfUnder9 =
        "CASE WHEN jsonb_typeof(e->'Value') = 'number' THEN (e->'Value')::numeric < 9 ELSE false END";

    private static string AsArray(string json) =>
        $"CASE WHEN jsonb_typeof({json}) = 'array' THEN {json} ELSE '[]'::jsonb END";

    private static string AnyRaw(string json, string test) =>
        $"EXISTS (SELECT 1 FROM jsonb_array_elements({AsArray(json)}) e WHERE {test})";

    private static string SameStore(string a, string b) =>
        $"({a}.legacy_id = {b}.legacy_id OR ({a}.correlation_id = {b}.correlation_id AND {a}.profile_name = {b}.profile_name))";

    private static string Owner(string alias, string units) => $"""
        EXISTS (
            SELECT 1 FROM therapy_settings ts
            WHERE ts.tenant_id = t.id
              AND ts.deleted_at IS NULL
              AND {units}
              AND {SameStore("ts", alias)})
        """;

    private static string MmolOwner(string alias) => Owner(alias, "lower(ts.units) IN ('mmol', 'mmol/l')");

    private static string MgdlOwner(string alias) =>
        Owner(alias, "lower(COALESCE(ts.units, '')) NOT IN ('mmol', 'mmol/l')");

    private static string MaybeRawIsf(string si) => $"""
        ({AnyRaw($"{si}.entries_json", RawIsf)}
         AND NOT ({MgdlOwner(si)} AND NOT {MmolOwner(si)})
         AND NOT (EXISTS (SELECT 1 FROM target_range_schedules x
                          WHERE x.tenant_id = t.id AND x.deleted_at IS NULL AND {SameStore("x", si)})
                  AND NOT EXISTS (SELECT 1 FROM target_range_schedules x
                                  WHERE x.tenant_id = t.id AND x.deleted_at IS NULL AND {SameStore("x", si)}
                                    AND (x.timestamp IS DISTINCT FROM {si}.timestamp
                                         OR x.sys_updated_at > {si}.sys_updated_at + interval '1 minute'
                                         OR {AnyRaw("x.entries_json", AnyRawTarget)}))))
        """;

    private static string Listed(string alias) =>
        $"strpos({alias}.profile_name, '{TherapySettings.ProfileSwitchStoreMarker}') = 0";

    private static readonly string DetectSql = $"""
        WITH t AS (SELECT @tenant AS id)
        SELECT 'TargetRange' AS "Kind", tr.id AS "Id", tr.profile_name AS "ProfileName", tr.timestamp AS "Timestamp",
               tr.legacy_id AS "LegacyId", tr.correlation_id AS "CorrelationId", false AS "CanDismiss",
               COALESCE(tr.entries_json, '[]'::jsonb)::text AS "EntriesJson"
        FROM target_range_schedules tr CROSS JOIN t
        WHERE tr.tenant_id = t.id
          AND tr.deleted_at IS NULL
          AND {Listed("tr")}
          AND NOT ({MgdlOwner("tr")} AND NOT {MmolOwner("tr")})
          AND {AnyRaw("tr.entries_json", AnyRawTarget)}
        UNION ALL
        SELECT 'Sensitivity', si.id, si.profile_name, si.timestamp, si.legacy_id, si.correlation_id,
               {Owner("si", "true")},
               COALESCE(si.entries_json, '[]'::jsonb)::text
        FROM sensitivity_schedules si CROSS JOIN t
        WHERE si.tenant_id = t.id
          AND si.deleted_at IS NULL
          AND {Listed("si")}
          AND ({MaybeRawIsf("si")} OR ({MmolOwner("si")} AND {AnyRaw("si.entries_json", IsfUnder9)}))
          AND NOT {Owner("si", $"ts.additional_properties->>'{IsfIsMgdlKey}' = 'true'")}
        ORDER BY "Kind" DESC, "ProfileName", "Timestamp", "Id"
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawMmolSchedule>> DetectAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = await db.Database
            .SqlQueryRaw<Row>(DetectSql, new NpgsqlParameter("tenant", db.TenantId))
            .ToListAsync(ct);

        return rows.Select(row => new RawMmolSchedule
        {
            Kind = row.Kind == "TargetRange" ? RawMmolScheduleKind.TargetRange : RawMmolScheduleKind.Sensitivity,
            Id = row.Id,
            ProfileName = row.ProfileName,
            Timestamp = row.Timestamp,
            LegacyId = row.LegacyId,
            CorrelationId = row.CorrelationId,
            Reason = row.Kind == "TargetRange" ? RawMmolReason.TargetInMmolBand : RawMmolReason.IsfMayBeMmol,
            CanDismiss = row.CanDismiss,
            Entries = Entries(row.EntriesJson),
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DismissIsfAsync(Guid sensitivityScheduleId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var isf = await db.SensitivitySchedules.AsNoTracking()
            .Where(s => s.Id == sensitivityScheduleId)
            .Select(s => new { s.LegacyId, s.CorrelationId, s.ProfileName })
            .FirstOrDefaultAsync(ct);
        if (isf is null)
            return false;

        var owners = await db.TherapySettings
            .Where(ts => (isf.LegacyId != null && ts.LegacyId == isf.LegacyId)
                || (isf.CorrelationId != null && ts.CorrelationId == isf.CorrelationId && ts.ProfileName == isf.ProfileName))
            .ToListAsync(ct);
        if (owners.Count == 0)
            return false;

        foreach (var owner in owners)
        {
            var properties = (owner.AdditionalPropertiesJson is { } json ? JsonNode.Parse(json) as JsonObject : null)
                ?? new JsonObject();
            properties[IsfIsMgdlKey] = true;
            owner.AdditionalPropertiesJson = properties.ToJsonString();
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static List<RawMmolScheduleEntry> Entries(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        return document.RootElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => new RawMmolScheduleEntry
            {
                Time = e.TryGetProperty("Time", out var time) && time.ValueKind == JsonValueKind.String
                    ? time.GetString()
                    : null,
                Low = Number(e, "Low"),
                High = Number(e, "High"),
                Value = Number(e, "Value"),
            })
            .ToList();
    }

    private static double? Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private sealed class Row
    {
        public string Kind { get; init; } = string.Empty;
        public Guid Id { get; init; }
        public string ProfileName { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public string? LegacyId { get; init; }
        public Guid? CorrelationId { get; init; }
        public bool CanDismiss { get; init; }
        public string EntriesJson { get; init; } = "[]";
    }
}
