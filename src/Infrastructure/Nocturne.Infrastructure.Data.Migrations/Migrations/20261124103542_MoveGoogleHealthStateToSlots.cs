using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveGoogleHealthStateToSlots : Migration
    {
        /// <summary>
        /// Moves Google Health's state out of the one <c>sync_cursors</c> key it kept it in, inside its
        /// own row. That key, <c>health</c>, was <c>{"LastGuid": "&lt;a JSON object as text&gt;"}</c>:
        /// its <c>lastSyncedTo</c>, <c>backfillCursorDate</c> and <c>backfillFloorDate</c> go to
        /// <c>backfill_low_water_marks</c> as UTC times, and its <c>backfillChunkDays</c> and
        /// <c>inventory</c> stay in <c>sync_cursors</c> as keys of their own. The coordinator's
        /// <c>googleHealthProgress</c> and <c>googleHealthDisconnect</c> keys go to
        /// <c>google_health_work</c>. The same five backfill keys are removed from <c>configuration</c>,
        /// where the connector kept them before it moved them to <c>sync_cursors</c> and has not read
        /// them since.
        /// </summary>
        /// <remarks>
        /// <c>backfillComplete</c> is not moved: the runtime reads the backfill as complete when its
        /// cursor has reached its floor, which every write of the flag also held. A row that says
        /// complete while its cursor is above its floor, or with a floor that does not read, gets the
        /// cursor as its floor, so it stays complete rather than crawling on to 1 January 2000.
        /// A value that does not read as what the runtime reads is dropped and the rest of the row
        /// kept, so the migration never raises on a row: the runtime already read each such value as
        /// absent. That is a time that is not a timestamp between years 1 and 9999, a chunk size that
        /// is not a whole number in integer range, an inventory that is not an array, a progress whose
        /// phase is not one of the import's phases or whose counts are not whole numbers, and a
        /// disconnect without two ids. A <c>health</c> whose text is not a JSON object is dropped
        /// whole. Rows of other connectors are not read, and a row already in the new shape has none
        /// of the moved keys, so a second run changes nothing. Dates without an offset read as UTC,
        /// as the runtime read them. FORCE ROW LEVEL SECURITY binds the migrator too, hence the
        /// per-tenant GUC. The block ends with the tenant unset and the session's time zone back, so
        /// a later migration in the same transaction inherits neither.
        /// </remarks>
        internal const string MoveSql = """
            DO $$
            DECLARE
                t RECORD;
                r RECORD;
                time_key text;
                vendor jsonb;
                resume jsonb;
                health jsonb;
                moved jsonb;
                stamped timestamptz;
                cursor_at timestamptz;
                floor_at timestamptz;
                progress jsonb;
                disconnect jsonb;
                phase text;
                counts integer[];
                count_key text;
                worker boolean;
                request_id uuid;
                subject_id uuid;
                dropped integer := 0;
                prior_time_zone text := current_setting('TimeZone');
            BEGIN
                PERFORM set_config('TimeZone', 'UTC', true);

                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);

                    UPDATE connector_configurations
                    SET configuration = configuration
                        - ARRAY['lastSyncedTo', 'backfillCursorDate', 'backfillFloorDate', 'backfillComplete', 'backfillChunkDays']
                    WHERE connector_name = 'googlehealth' AND jsonb_typeof(configuration) = 'object'
                      AND configuration ?| ARRAY['lastSyncedTo', 'backfillCursorDate', 'backfillFloorDate', 'backfillComplete', 'backfillChunkDays'];

                    FOR r IN
                        SELECT id, sync_cursors, backfill_low_water_marks
                        FROM connector_configurations
                        WHERE connector_name = 'googlehealth' AND jsonb_typeof(sync_cursors) = 'object'
                          AND sync_cursors ?| ARRAY['health', 'googleHealthProgress', 'googleHealthDisconnect']
                    LOOP
                        vendor := r.sync_cursors;
                        resume := CASE
                            WHEN jsonb_typeof(r.backfill_low_water_marks) = 'object' THEN r.backfill_low_water_marks
                            ELSE '{}'::jsonb
                        END;

                        IF vendor ? 'health' THEN
                            health := NULL;
                            BEGIN
                                health := (vendor -> 'health' ->> 'LastGuid')::jsonb;
                            EXCEPTION WHEN others THEN
                                health := NULL;
                            END;
                            vendor := vendor - 'health';

                            IF jsonb_typeof(health) IS DISTINCT FROM 'object' THEN
                                IF jsonb_typeof(r.sync_cursors -> 'health' -> 'LastGuid') IS DISTINCT FROM 'null' THEN
                                    dropped := dropped + 1;
                                END IF;
                            ELSE
                                moved := '{}'::jsonb;
                                cursor_at := NULL;
                                floor_at := NULL;
                                FOREACH time_key IN ARRAY ARRAY['lastSyncedTo', 'backfillCursorDate', 'backfillFloorDate'] LOOP
                                    CONTINUE WHEN jsonb_typeof(health -> time_key) IS NULL OR jsonb_typeof(health -> time_key) = 'null';

                                    stamped := NULL;
                                    BEGIN
                                        IF jsonb_typeof(health -> time_key) = 'string' THEN
                                            stamped := (health ->> time_key)::timestamptz;
                                        END IF;
                                    EXCEPTION WHEN others THEN
                                        stamped := NULL;
                                    END;

                                    IF stamped IS NULL OR NOT isfinite(stamped)
                                       OR extract(year FROM stamped AT TIME ZONE 'UTC') NOT BETWEEN 1 AND 9999 THEN
                                        dropped := dropped + 1;
                                    ELSE
                                        IF time_key = 'backfillCursorDate' THEN cursor_at := stamped; END IF;
                                        IF time_key = 'backfillFloorDate' THEN floor_at := stamped; END IF;
                                        moved := moved || jsonb_build_object(time_key,
                                            to_char(stamped AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
                                    END IF;
                                END LOOP;

                                IF health -> 'backfillComplete' = 'true'::jsonb AND cursor_at IS NOT NULL
                                   AND (floor_at IS NULL OR cursor_at > floor_at) THEN
                                    moved := moved || jsonb_build_object('backfillFloorDate',
                                        to_char(cursor_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
                                END IF;
                                resume := resume || moved;

                                IF health ? 'backfillChunkDays' AND jsonb_typeof(health -> 'backfillChunkDays') <> 'null' THEN
                                    IF jsonb_typeof(health -> 'backfillChunkDays') = 'number'
                                       AND (health ->> 'backfillChunkDays')::numeric = trunc((health ->> 'backfillChunkDays')::numeric)
                                       AND (health ->> 'backfillChunkDays')::numeric BETWEEN -2147483648 AND 2147483647 THEN
                                        vendor := vendor || jsonb_build_object('backfillChunkDays',
                                            (health ->> 'backfillChunkDays')::numeric::integer);
                                    ELSE
                                        dropped := dropped + 1;
                                    END IF;
                                END IF;

                                IF health ? 'inventory' AND jsonb_typeof(health -> 'inventory') <> 'null' THEN
                                    IF jsonb_typeof(health -> 'inventory') = 'array' THEN
                                        vendor := vendor || jsonb_build_object('inventory', health -> 'inventory');
                                    ELSE
                                        dropped := dropped + 1;
                                    END IF;
                                END IF;
                            END IF;
                        END IF;

                        progress := NULL;
                        counts := ARRAY[0, 0, 0];
                        worker := false;
                        IF vendor ? 'googleHealthProgress' THEN
                            progress := vendor -> 'googleHealthProgress';
                            vendor := vendor - 'googleHealthProgress';
                            phase := CASE WHEN jsonb_typeof(progress) = 'object' THEN progress ->> 'Phase' END;
                            IF phase IS NULL OR phase NOT IN ('queued', 'preparing', 'refreshing_session', 'reading', 'validating', 'integrating')
                               OR (jsonb_typeof(progress -> 'DataType') IS NOT NULL
                                   AND jsonb_typeof(progress -> 'DataType') NOT IN ('string', 'null'))
                               OR (jsonb_typeof(progress -> 'WorkerOwned') IS NOT NULL
                                   AND jsonb_typeof(progress -> 'WorkerOwned') <> 'boolean') THEN
                                progress := NULL;
                            ELSE
                                FOR i IN 1..3 LOOP
                                    count_key := (ARRAY['CompletedDataTypes', 'TotalDataTypes', 'PagesRead'])[i];
                                    CONTINUE WHEN jsonb_typeof(progress -> count_key) IS NULL;
                                    IF jsonb_typeof(progress -> count_key) = 'number'
                                       AND (progress ->> count_key)::numeric = trunc((progress ->> count_key)::numeric)
                                       AND (progress ->> count_key)::numeric BETWEEN -2147483648 AND 2147483647 THEN
                                        counts[i] := (progress ->> count_key)::numeric::integer;
                                    ELSE
                                        progress := NULL;
                                        EXIT;
                                    END IF;
                                END LOOP;
                                worker := coalesce((progress ->> 'WorkerOwned')::boolean, false);
                            END IF;
                            IF progress IS NULL THEN
                                counts := ARRAY[0, 0, 0];
                                worker := false;
                                dropped := dropped + 1;
                            END IF;
                        END IF;

                        disconnect := NULL;
                        request_id := NULL;
                        subject_id := NULL;
                        IF vendor ? 'googleHealthDisconnect' THEN
                            disconnect := vendor -> 'googleHealthDisconnect';
                            vendor := vendor - 'googleHealthDisconnect';
                            BEGIN
                                request_id := (disconnect ->> 'RequestId')::uuid;
                                subject_id := (disconnect ->> 'SubjectId')::uuid;
                            EXCEPTION WHEN others THEN
                                request_id := NULL;
                            END;
                            IF jsonb_typeof(disconnect) IS DISTINCT FROM 'object' OR request_id IS NULL OR subject_id IS NULL THEN
                                disconnect := NULL;
                                dropped := dropped + 1;
                            END IF;
                        END IF;

                        IF progress IS NOT NULL OR disconnect IS NOT NULL THEN
                            INSERT INTO google_health_work (tenant_id, progress_phase, progress_data_type,
                                progress_completed_data_types, progress_total_data_types, progress_pages_read,
                                progress_worker_owned, disconnect_request_id, disconnect_subject_id)
                            VALUES (t.id,
                                CASE WHEN progress IS NOT NULL THEN phase END,
                                CASE WHEN progress IS NOT NULL THEN progress ->> 'DataType' END,
                                counts[1], counts[2], counts[3], coalesce(worker, false),
                                CASE WHEN disconnect IS NOT NULL THEN request_id END,
                                CASE WHEN disconnect IS NOT NULL THEN subject_id END)
                            ON CONFLICT (tenant_id) DO NOTHING;
                        END IF;

                        UPDATE connector_configurations
                        SET sync_cursors = vendor,
                            backfill_low_water_marks = NULLIF(resume, '{}'::jsonb)
                        WHERE id = r.id;
                    END LOOP;
                END LOOP;

                PERFORM set_config('app.current_tenant_id', '', true);
                PERFORM set_config('TimeZone', prior_time_zone, true);

                IF dropped > 0 THEN
                    RAISE NOTICE 'Google Health state move dropped % value(s) that could not be read', dropped;
                END IF;
            END $$;
            """;

        /// <summary>
        /// Puts the state back where the image before <see cref="MoveSql"/> reads it: <c>health</c>
        /// rebuilt from the three resume times and the two vendor keys, with <c>backfillComplete</c>
        /// as the runtime derived it, and the work row as the coordinator's two keys. The keys
        /// removed from <c>configuration</c> and the values <see cref="MoveSql"/> dropped are not
        /// restored: nothing read either. It ends with the tenant unset, as <see cref="MoveSql"/> does.
        /// </summary>
        internal const string RestoreSql = """
            DO $$
            DECLARE
                t RECORD;
                r RECORD;
                w RECORD;
                moved_key text;
                vendor jsonb;
                resume jsonb;
                health jsonb;
                cursor_at timestamptz;
                floor_at timestamptz;
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);

                    FOR r IN
                        SELECT id, sync_cursors, backfill_low_water_marks
                        FROM connector_configurations
                        WHERE connector_name = 'googlehealth'
                    LOOP
                        vendor := CASE WHEN jsonb_typeof(r.sync_cursors) = 'object' THEN r.sync_cursors ELSE '{}'::jsonb END;
                        resume := CASE
                            WHEN jsonb_typeof(r.backfill_low_water_marks) = 'object' THEN r.backfill_low_water_marks
                            ELSE '{}'::jsonb
                        END;

                        health := '{}'::jsonb;
                        FOREACH moved_key IN ARRAY ARRAY['lastSyncedTo', 'backfillCursorDate', 'backfillFloorDate'] LOOP
                            IF resume ? moved_key THEN
                                health := health || jsonb_build_object(moved_key, resume -> moved_key);
                            END IF;
                        END LOOP;
                        FOREACH moved_key IN ARRAY ARRAY['backfillChunkDays', 'inventory'] LOOP
                            IF vendor ? moved_key THEN
                                health := health || jsonb_build_object(moved_key, vendor -> moved_key);
                            END IF;
                        END LOOP;
                        IF health ? 'backfillCursorDate' THEN
                            cursor_at := (health ->> 'backfillCursorDate')::timestamptz;
                            floor_at := coalesce((health ->> 'backfillFloorDate')::timestamptz, '2000-01-01T00:00:00Z'::timestamptz);
                            health := health || jsonb_build_object('backfillComplete', cursor_at <= floor_at);
                        END IF;

                        vendor := vendor - 'backfillChunkDays' - 'inventory';
                        resume := resume - 'lastSyncedTo' - 'backfillCursorDate' - 'backfillFloorDate';
                        IF health <> '{}'::jsonb THEN
                            vendor := vendor || jsonb_build_object('health',
                                jsonb_build_object('LastUpdatedAt', NULL, 'LastGuid', health::text));
                        END IF;

                        SELECT * INTO w FROM google_health_work WHERE tenant_id = t.id;
                        IF FOUND THEN
                            IF w.progress_phase IS NOT NULL THEN
                                vendor := vendor || jsonb_build_object('googleHealthProgress', jsonb_build_object(
                                    'Phase', w.progress_phase,
                                    'DataType', w.progress_data_type,
                                    'CompletedDataTypes', w.progress_completed_data_types,
                                    'TotalDataTypes', w.progress_total_data_types,
                                    'PagesRead', w.progress_pages_read,
                                    'WorkerOwned', w.progress_worker_owned));
                            END IF;
                            IF w.disconnect_request_id IS NOT NULL AND w.disconnect_subject_id IS NOT NULL THEN
                                vendor := vendor || jsonb_build_object('googleHealthDisconnect', jsonb_build_object(
                                    'RequestId', w.disconnect_request_id,
                                    'SubjectId', w.disconnect_subject_id));
                            END IF;
                        END IF;

                        UPDATE connector_configurations
                        SET sync_cursors = CASE WHEN r.sync_cursors IS NULL AND vendor = '{}'::jsonb THEN NULL ELSE vendor END,
                            backfill_low_water_marks = NULLIF(resume, '{}'::jsonb)
                        WHERE id = r.id;
                    END LOOP;
                END LOOP;

                PERFORM set_config('app.current_tenant_id', '', true);
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "google_health_work",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    progress_phase = table.Column<string>(type: "text", nullable: true),
                    progress_data_type = table.Column<string>(type: "text", nullable: true),
                    progress_completed_data_types = table.Column<int>(type: "integer", nullable: false),
                    progress_total_data_types = table.Column<int>(type: "integer", nullable: false),
                    progress_pages_read = table.Column<int>(type: "integer", nullable: false),
                    progress_worker_owned = table.Column<bool>(type: "boolean", nullable: false),
                    disconnect_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    disconnect_subject_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_health_work", x => x.tenant_id);
                    table.ForeignKey(
                        name: "FK_google_health_work_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("ALTER TABLE google_health_work ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE google_health_work FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON google_health_work;
                CREATE POLICY tenant_isolation ON google_health_work
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql(MoveSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RestoreSql);

            migrationBuilder.DropTable(
                name: "google_health_work");
        }
    }
}
