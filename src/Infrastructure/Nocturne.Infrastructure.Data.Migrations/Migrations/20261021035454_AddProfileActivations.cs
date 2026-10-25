using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Creates <c>profile_activations</c> and converts every Profile span into one, keeping the span's
    /// id, stamps, legacy or upstream key and soft-delete state, so every read serves the same
    /// identifiers. The spans and the <c>@@@@@</c> profile rows decomposed beside them stay where they
    /// are; no listing or resolver reads them.
    /// </summary>
    /// <remarks>
    /// The columns follow <c>ProfileActivationLegacyFields.FromSpanMetadata</c>, which reads a span's
    /// metadata as <c>ProfileSpanMetadata</c> does, and the snapshot follows
    /// <c>ProfileActivationSnapshots</c>, built from the span's own <c>profileJson</c>: the source the
    /// <c>@@@@@</c> rows were decomposed from. A span the legacy treatment reads served (an
    /// <c>original_id</c> and a <c>utcOffset</c>) keeps its key as the legacy id; any other with a key
    /// is a connector's, effective, keyed by sync identifier. A live duplicate of a key keeps its newest
    /// row; the others are soft-deleted, as the unique indexes require. A value out of the range of its
    /// column converts as absent, and a span whose snapshot cannot be built keeps none, so the metadata of
    /// no span can stop the migration. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant
    /// GUC; each statement names the tenant too, so a role that bypasses RLS still converts each tenant once.
    /// </remarks>
    public partial class AddProfileActivations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "profile_activations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    end_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    base_profile_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    percentage = table.Column<int>(type: "integer", nullable: false),
                    timeshift_ms = table.Column<long>(type: "bigint", nullable: false),
                    requested_duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    requested_activation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    snapshot_is_customized = table.Column<bool>(type: "boolean", nullable: false),
                    entered_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sync_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    patient_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    upstream_fingerprint = table.Column<string>(type: "text", nullable: true),
                    deleted_by_user = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_activations", x => x.id);
                    table.ForeignKey(
                        name: "FK_profile_activations_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_profile_activations_patient_devices_patient_device_id",
                        column: x => x.patient_device_id,
                        principalTable: "patient_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_profile_activations_profile_activations_requested_activatio~",
                        column: x => x.requested_activation_id,
                        principalTable: "profile_activations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_profile_activations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_profile_activations_correlation_id",
                table: "profile_activations",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_profile_activations_device_id",
                table: "profile_activations",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_profile_activations_patient_device_id",
                table: "profile_activations",
                column: "patient_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_profile_activations_requested_activation_id",
                table: "profile_activations",
                column: "requested_activation_id");

            migrationBuilder.CreateIndex(
                name: "ix_profile_activations_tenant_history",
                table: "profile_activations",
                columns: new[] { "tenant_id", "sys_updated_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_profile_activations_tenant_legacy_id",
                table: "profile_activations",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_profile_activations_tenant_legacy_id_user_deleted",
                table: "profile_activations",
                columns: new[] { "tenant_id", "legacy_id" },
                filter: "legacy_id IS NOT NULL AND deleted_by_user");

            migrationBuilder.CreateIndex(
                name: "ix_profile_activations_tenant_source_sync_id",
                table: "profile_activations",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_profile_activations_tenant_start_timestamp",
                table: "profile_activations",
                columns: new[] { "tenant_id", "start_timestamp" },
                descending: new[] { false, true },
                filter: "deleted_at IS NULL");

            // NULLIF + missing_ok keeps the policy expression safe to evaluate when the GUC is
            // unset, matching every other tenant-scoped table.
            migrationBuilder.Sql("ALTER TABLE profile_activations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE profile_activations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON profile_activations;
                CREATE POLICY tenant_isolation ON profile_activations
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql(ConvertProfileSpansSql);
        }

        /// <summary>
        /// A metadata value as a number, as <c>TryReadDecimal</c> reads it, else null; a string too long
        /// or with too large an exponent for <c>numeric</c> is not one.
        /// </summary>
        private static string Num(string json) =>
            $@"
            (CASE WHEN jsonb_typeof({json}) = 'number' THEN ({json} #>> '{{}}')::numeric
                  WHEN jsonb_typeof({json}) = 'string' AND length({json} #>> '{{}}') <= 1000
                       AND btrim({json} #>> '{{}}') ~ '^[-+]?([0-9]+[.]?[0-9]*|[.][0-9]+)([eE][-+]?[0-9]{{1,4}})?$'
                  THEN btrim({json} #>> '{{}}')::numeric END)
            ";

        /// <summary>A numeric as <c>int</c>, or null outside its range.</summary>
        private static string Int(string numeric) =>
            $"(CASE WHEN ({numeric}) BETWEEN -2147483648 AND 2147483647 THEN ({numeric})::int END)";

        /// <summary>A numeric as <c>bigint</c>, or null outside its range.</summary>
        private static string BigInt(string numeric) =>
            $"(CASE WHEN ({numeric}) BETWEEN -9223372036854775808 AND 9223372036854775807 THEN ({numeric})::bigint END)";

        /// <summary>A length in milliseconds, or null past a thousand years, which no timestamp can end.</summary>
        private static string Length(string numeric) =>
            $"(CASE WHEN ({numeric}) <= 31557600000000 THEN ({numeric})::bigint END)";

        /// <summary>A numeric a <c>double</c> reads back finitely, else null.</summary>
        private static string Dbl(string numeric) =>
            $"(CASE WHEN abs({numeric}) <= 1e300 THEN {numeric} END)";

        private const string AapsEnteredBy = "openaps://AndroidAPS%";

        /// <summary>
        /// The <c>ProfileActivationSource</c> key of an activation: its data source, else AAPS by app or
        /// uploader, else its app or uploader.
        /// </summary>
        private static string Family(string row) =>
            $@"COALESCE(NULLIF({row}.data_source, ''),
                        CASE WHEN lower({row}.app) = 'aaps' OR {row}.entered_by ILIKE '{AapsEnteredBy}' THEN 'AAPS' END,
                        {row}.app, {row}.entered_by, '')";

        /// <summary>A metadata value as a string, as <c>TryReadString</c> reads it, else null.</summary>
        private static string Str(string json) =>
            $"(CASE WHEN jsonb_typeof({json}) = 'string' THEN {json} #>> '{{}}' END)";

        /// <summary>A profile time value's seconds from midnight, as <c>TimeValue.EnsureTimeAsSeconds</c> reads it.</summary>
        private static string Seconds(string entry) =>
            $@"
            COALESCE({Int($"trunc({Num($"{entry}->'timeAsSeconds'")})")},
                CASE WHEN ({entry}->>'time') ~ '^([01]?[0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9])?$'
                     THEN split_part({entry}->>'time', ':', 1)::int * 3600
                        + split_part({entry}->>'time', ':', 2)::int * 60
                        + COALESCE(NULLIF(split_part({entry}->>'time', ':', 3), '')::int, 0) END)
            ";

        /// <summary>A profile schedule as the snapshot's entries, sensitivity converted to mg/dL when the profile is mmol.</summary>
        private static string Entries(string profile, string key, bool toMgdl)
        {
            var value = $"COALESCE({Dbl(Num("e->'value'"))}, 0)";
            if (toMgdl)
                value = $"CASE WHEN lower({profile}->>'units') IN ('mmol', 'mmol/l') THEN round({value} * 18.0182) ELSE {value} END";
            return $@"
                (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                            'Time', COALESCE(e->>'time', '00:00'),
                            'Value', {value},
                            'TimeAsSeconds', {Seconds("e")}) ORDER BY o), '[]'::jsonb)
                 FROM jsonb_array_elements(CASE WHEN jsonb_typeof({profile}->'{key}') = 'array'
                                                THEN {profile}->'{key}' ELSE '[]'::jsonb END) WITH ORDINALITY x(e, o))
                ";
        }

        private static string Targets(string profile)
        {
            const string highs = "CASE WHEN jsonb_typeof(pj->'target_high') = 'array' THEN pj->'target_high' ELSE '[]'::jsonb END";
            var low = $"COALESCE({Dbl(Num("e->'value'"))}, 0)";
            var high = $@"
                COALESCE((SELECT {Dbl(Num("h->'value'"))} FROM jsonb_array_elements({highs.Replace("pj", profile)}) h
                          WHERE h->>'time' = e->>'time' LIMIT 1), {low})
                ";
            static string Mgdl(string profile, string value) =>
                $"CASE WHEN lower({profile}->>'units') IN ('mmol', 'mmol/l') THEN round(({value}) * 18.0182) ELSE ({value}) END";
            return $@"
                (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                            'Time', COALESCE(e->>'time', '00:00'),
                            'Low', {Mgdl(profile, low)},
                            'High', {Mgdl(profile, high)},
                            'TimeAsSeconds', {Seconds("e")}) ORDER BY o), '[]'::jsonb)
                 FROM jsonb_array_elements(CASE WHEN jsonb_typeof({profile}->'target_low') = 'array'
                                                THEN {profile}->'target_low' ELSE '[]'::jsonb END) WITH ORDINALITY x(e, o))
                ";
        }

        /// <summary>The <c>icfg</c> insulin a span's metadata carries, as <c>TreatmentInsulinContext</c>, else null.</summary>
        private static readonly string Insulin = $@"
            CASE WHEN {Dbl(Num("m->'insulinDia'"))} > 0 AND {Int($"trunc({Num("m->'insulinPeak'")})")} > 0 THEN jsonb_build_object(
                'PatientInsulinId', '00000000-0000-0000-0000-000000000000',
                'InsulinName', COALESCE({Str("m->'insulinName'")}, ''),
                'Dia', {Num("m->'insulinDia'")},
                'Peak', {Int($"trunc({Num("m->'insulinPeak'")})")},
                'Curve', COALESCE({Str("m->'insulinCurve'")}, 'rapid-acting'),
                'Concentration', COALESCE({Int($"trunc(COALESCE({Num("m->'insulinConcentration'")}, 100))")}, 100)) END
            ";

        private static readonly string ConvertProfileSpansSql = ConvertProfileSpans(unconvertedOnly: false);

        /// <summary>
        /// The conversion <see cref="Up"/> runs over every Profile span. With
        /// <paramref name="unconvertedOnly"/> it converts only the spans no activation holds the id of,
        /// as <c>RemoveLegacyProfileSpansAndSwitchSnapshots</c> needs for spans a pre-cutover image wrote
        /// after this ran: a span whose key a live activation already holds converts as a deleted loser,
        /// and a tenant whose conversion fails is left unconverted with a warning rather than aborting.
        /// </summary>
        internal static string ConvertProfileSpans(bool unconvertedOnly)
        {
            var declare = unconvertedOnly ? @"
                pending uuid[];" : "";
            var prelude = unconvertedOnly ? @"
                    BEGIN
                    pending := ARRAY(
                        SELECT s.id FROM state_spans s
                        WHERE s.tenant_id = t.id AND s.category = 'Profile'
                          AND NOT EXISTS (SELECT 1 FROM profile_activations a WHERE a.tenant_id = t.id AND a.id = s.id));
                    CONTINUE WHEN cardinality(pending) = 0;" : "";
            var spanFilter = unconvertedOnly ? " AND s.id = ANY(pending)" : "";
            var keyHeld = unconvertedOnly ? @"
                                OR (v.deleted_at IS NULL AND EXISTS (
                                        SELECT 1 FROM profile_activations x
                                        WHERE x.tenant_id = t.id AND x.deleted_at IS NULL
                                          AND (x.legacy_id = v.legacy_id
                                               OR (x.sync_identifier = v.sync_identifier
                                                   AND x.data_source IS NOT DISTINCT FROM v.data_source))))" : "";
            var requestFilter = unconvertedOnly ? " AND e.id = ANY(pending)" : "";
            var epilogue = unconvertedOnly ? @"
                    EXCEPTION WHEN OTHERS THEN
                        RAISE WARNING 'Profile spans of tenant % stay unconverted: %', t.id, SQLERRM;
                    END;" : "";
            return $@"
            DO $$
            DECLARE
                t RECORD;
                span_ids uuid[];
                span_id uuid;
                m jsonb;
                pj jsonb;{declare}
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);{prelude}

                    WITH spans AS (
                        SELECT s.*, COALESCE(s.metadata, '{{}}'::jsonb) AS m,
                               (s.original_id IS NOT NULL AND COALESCE(s.metadata, '{{}}'::jsonb) ? 'utcOffset') AS served
                        FROM state_spans s
                        WHERE s.tenant_id = t.id AND s.category = 'Profile'{spanFilter}),
                    read AS (
                        SELECT sp.*,
                               (sp.m ? 'eventType') AS effective,
                               (NOT sp.served AND sp.original_id IS NOT NULL) AS connector,
                               NULLIF({Str("sp.m->'originalProfileName'")}, '') AS original_name,
                               {Str("sp.m->'enteredBy'")} AS entered_by,
                               {Str("sp.m->'app'")} AS app,
                               {Num("sp.m->'timeshift'")} AS timeshift,
                               CASE WHEN {Str("sp.m->'timeshiftUnit'")} IN ('ms', 'h') THEN sp.m->>'timeshiftUnit' END AS unit_key
                        FROM spans sp),
                    classified AS (
                        SELECT rd.*,
                               COALESCE(rd.original_name, NULLIF({Str("rd.m->'profileName'")}, ''), 'Default') AS base_name,
                               (rd.original_name IS NOT NULL
                                    OR rd.entered_by ILIKE 'openaps://AndroidAPS%'
                                    OR lower(rd.app) = 'aaps') AS aaps,
                               CASE WHEN rd.effective OR rd.unit_key IS NOT NULL OR rd.timeshift IS NULL THEN rd.unit_key
                                    WHEN rd.original_name IS NOT NULL
                                         OR rd.entered_by ILIKE 'openaps://AndroidAPS%'
                                         OR lower(rd.app) = 'aaps'
                                         OR abs(rd.timeshift) > 24 THEN 'ms'
                                    ELSE 'h' END AS unit,
                               CASE WHEN rd.unit_key IS NOT NULL THEN {Num("rd.m->'uploadedTimeshift'")}
                                    WHEN NOT rd.effective THEN rd.timeshift END AS uploaded
                        FROM read rd),
                    converted AS (
                        SELECT c.*,
                               COALESCE({BigInt($@"
                                   CASE WHEN c.effective THEN COALESCE(trunc({Num("c.m->'originalTimeshift'")}), round(COALESCE(c.timeshift, 0) * 3600000))
                                        WHEN c.unit = 'ms' AND c.uploaded IS NOT NULL THEN round(c.uploaded)
                                        WHEN c.unit_key IS NOT NULL THEN round(COALESCE(c.timeshift, 0) * 3600000)
                                        WHEN c.unit = 'h' THEN round(c.uploaded * 3600000)
                                        ELSE 0 END")}, 0) AS timeshift_ms,
                               COALESCE({Int($@"
                                   CASE WHEN c.effective THEN COALESCE(trunc({Num("c.m->'originalPercentage'")}), round({Num("c.m->'percentage'")}))
                                        ELSE round({Num("c.m->'percentage'")}) END")}, 100) AS percentage,
                               {Length($@"CASE WHEN c.connector THEN NULL
                                    WHEN c.effective THEN CASE WHEN {Num("c.m->'originalDuration'")} > 0 THEN trunc({Num("c.m->'originalDuration'")}) END
                                    WHEN {Num("c.m->'duration'")} IS NOT NULL
                                        THEN CASE WHEN {Num("c.m->'duration'")} > 0 THEN round({Num("c.m->'duration'")} * 60000) END
                                    WHEN {Num("c.m->'durationInMilliseconds'")} IS NOT NULL
                                        THEN CASE WHEN {Num("c.m->'durationInMilliseconds'")} > 0 THEN trunc({Num("c.m->'durationInMilliseconds'")}) END
                                    WHEN c.end_timestamp > c.start_timestamp AND c.superseded_by_id IS NULL
                                        THEN round(extract(epoch FROM c.end_timestamp - c.start_timestamp) * 1000)
                                    END")} AS requested_duration_ms,
                               CASE WHEN c.served THEN c.original_id END AS legacy_id,
                               CASE WHEN NOT c.served THEN c.original_id END AS sync_identifier,
                               CASE WHEN NOT c.served THEN c.source
                                    WHEN c.source IS NULL OR c.source IN (c.entered_by, c.app, 'nightscout') THEN NULL
                                    ELSE c.source END AS data_source
                        FROM classified c),
                    ranked AS (
                        SELECT v.*,
                               (v.deleted_at IS NULL AND COALESCE(v.legacy_id, v.sync_identifier) IS NOT NULL
                                AND row_number() OVER (
                                        PARTITION BY v.deleted_at IS NULL, v.legacy_id,
                                                     CASE WHEN v.legacy_id IS NULL THEN v.data_source END, v.sync_identifier
                                        ORDER BY v.updated_at DESC, v.id DESC) > 1){keyHeld} AS loser
                        FROM converted v),
                    numbered AS (
                        SELECT k.*, row_number() OVER (PARTITION BY k.loser ORDER BY k.id) - 1 AS loser_index
                        FROM ranked k)
                    INSERT INTO profile_activations (
                        id, tenant_id, start_timestamp, end_timestamp, phase, origin, base_profile_name,
                        percentage, timeshift_ms, requested_duration_ms, snapshot, snapshot_is_customized,
                        entered_by, app, device, data_source, utc_offset, legacy_id, sync_identifier,
                        additional_properties, sys_created_at, sys_updated_at, deleted_at, deleted_by_user)
                    SELECT n.id, n.tenant_id, n.start_timestamp, n.end_timestamp,
                           CASE WHEN n.effective OR n.connector THEN 'Effective' ELSE 'Requested' END,
                           CASE WHEN n.connector THEN 'Connector'
                                WHEN n.aaps OR n.entered_by ILIKE ANY (ARRAY['openaps://%', 'loop://%', 'trio%', 'iaps%', 'freeaps%'])
                                    THEN 'Uploader'
                                ELSE 'User' END,
                           n.base_name, n.percentage, n.timeshift_ms, n.requested_duration_ms,
                           CASE WHEN ({Insulin.Replace("m->", "n.m->")}) IS NOT NULL
                                THEN jsonb_build_object('Insulin', {Insulin.Replace("m->", "n.m->")}) END,
                           n.effective,
                           n.entered_by, n.app, CASE WHEN n.connector THEN NULL ELSE n.entered_by END,
                           n.data_source,
                           {Int($"trunc({Num("n.m->'utcOffset'")})")},
                           n.legacy_id, n.sync_identifier,
                           NULLIF(
                               CASE WHEN NOT n.effective AND n.unit IS NOT NULL AND n.uploaded IS NOT NULL
                                    THEN jsonb_build_object('uploadedTimeshift', n.uploaded, 'timeshiftUnit', n.unit)
                                    ELSE '{{}}'::jsonb END
                               || CASE WHEN NOT n.effective AND NOT n.connector AND NOT (n.m ? 'percentage')
                                       THEN jsonb_build_object('percentageOmitted', true)
                                       ELSE '{{}}'::jsonb END
                               || (CASE WHEN {Str("n.m->'profileName'")} = n.base_name THEN n.m - 'profileName' ELSE n.m END
                                   - ARRAY['percentage', 'timeshift', 'originalPercentage', 'originalTimeshift', 'app',
                                           'enteredBy', 'utcOffset', 'insulinName', 'insulinDia', 'insulinPeak',
                                           'insulinConcentration', 'insulinCurve']
                                   - CASE WHEN n.effective THEN 'originalProfileName' ELSE '' END),
                               '{{}}'::jsonb),
                           n.created_at,
                           CASE WHEN n.loser THEN now() + n.loser_index * interval '1 microsecond' ELSE n.updated_at END,
                           CASE WHEN n.loser THEN now() ELSE n.deleted_at END,
                           n.deleted_by_user AND NOT n.loser
                    FROM numbered n;

                    span_ids := ARRAY(
                        SELECT s.id FROM state_spans s
                        WHERE s.tenant_id = t.id AND s.category = 'Profile'
                          AND jsonb_typeof(s.metadata->'profileJson') = 'string'{spanFilter});

                    FOREACH span_id IN ARRAY span_ids LOOP
                        SELECT s.metadata INTO m FROM state_spans s WHERE s.tenant_id = t.id AND s.id = span_id;
                        BEGIN
                            pj := (m->>'profileJson')::jsonb;
                        EXCEPTION WHEN invalid_text_representation THEN
                            pj := NULL;
                        END;

                        CONTINUE WHEN jsonb_typeof(pj) IS DISTINCT FROM 'object';

                        BEGIN
                            UPDATE profile_activations
                            SET snapshot = jsonb_build_object(
                                    'Basal', {Entries("pj", "basal", toMgdl: false)},
                                    'CarbRatio', {Entries("pj", "carbratio", toMgdl: false)},
                                    'Sensitivity', {Entries("pj", "sens", toMgdl: true)},
                                    'Targets', {Targets("pj")},
                                    'Units', {Str("pj->'units'")},
                                    'Dia', COALESCE({Dbl(Num("pj->'dia'"))}, 3),
                                    'CarbsHr', COALESCE({Int($"trunc({Num("pj->'carbs_hr'")})")}, 20),
                                    'Timezone', {Str("pj->'timezone'")},
                                    'Insulin', {Insulin})
                            WHERE tenant_id = t.id AND id = span_id;
                        EXCEPTION WHEN numeric_value_out_of_range OR invalid_text_representation THEN
                            RAISE NOTICE 'Profile span % keeps no snapshot: %', span_id, SQLERRM;
                        END;
                    END LOOP;

                    -- As ProfileActivationWriter confirms: the newest request of the family at or before the
                    -- effective switch, passing over temporary ones that ran out before it.
                    UPDATE profile_activations e
                    SET requested_activation_id = (
                        SELECT CASE WHEN f.runs THEN f.id END
                        FROM (
                            SELECT q.id, q.start_timestamp,
                                   (q.base_profile_name = e.base_profile_name
                                    AND q.percentage = e.percentage
                                    AND q.timeshift_ms = e.timeshift_ms) AS runs,
                                   COALESCE(q.requested_duration_ms > 0
                                       AND q.requested_duration_ms
                                           <= extract(epoch FROM e.start_timestamp - q.start_timestamp) * 1000, false) AS ran_out
                            FROM profile_activations q
                            WHERE q.tenant_id = t.id
                              AND q.phase = 'Requested'
                              AND q.deleted_at IS NULL
                              AND {Family("q")} = {Family("e")}
                              AND q.start_timestamp <= e.start_timestamp) f
                        WHERE f.runs OR NOT f.ran_out
                        ORDER BY f.start_timestamp DESC, f.id DESC
                        LIMIT 1)
                    WHERE e.tenant_id = t.id AND e.snapshot_is_customized{requestFilter};{epilogue}
                END LOOP;
            END $$;
            ";
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_activations");
        }
    }
}
