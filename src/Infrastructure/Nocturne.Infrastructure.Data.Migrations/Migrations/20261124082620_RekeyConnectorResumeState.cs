using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RekeyConnectorResumeState : Migration
    {
        /// <summary>
        /// Moves connector state into its two slots, inside each row: Glooko's schedule stamps
        /// (<c>full-walk</c>, <c>full-walk-attempt</c>, <c>lookback-read</c>) from <c>sync_cursors</c>,
        /// where each was <c>{"LastUpdatedAt": stamp}</c>, to <c>backfill_low_water_marks</c> as a UTC
        /// time; Tandem's published-profile fingerprints (<c>profile:*</c>) from
        /// <c>{"LastGuid": fingerprint}</c> to the bare fingerprint; and MyFitnessPal's
        /// <c>lastFullWalkAt</c> out of its secrets and configuration, since each secret is encrypted
        /// with the instance key and SQL cannot read it, so its next run walks.
        /// </summary>
        /// <remarks>
        /// A value that does not read as what it should be is dropped and the rest of its row kept, so
        /// the migration never raises on a row: a sync already reads each such value as absent, and a
        /// stamp outside the years a sync can read is dropped too. A <c>sync_cursors</c> that is not an
        /// object is left as it is, which a sync reads as empty. A Glooko
        /// <c>backfill_low_water_marks</c> that is not an object holds no mark a sync can read and would
        /// fail every read of the stamps, so it is replaced by them. A row already in the new shape is
        /// left as it is. Rows of other connectors are not read. Every other key of a Tandem row, such as
        /// the zone its last sync read the pump in (<c>timeZone</c>), is already vendor state and is kept.
        /// FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
        /// </remarks>
        internal const string RekeySql = """
            DO $$
            DECLARE
                t RECORD;
                r RECORD;
                stamp_key text;
                profile_key text;
                stamped_at timestamptz;
                vendor jsonb;
                resume jsonb;
                dropped integer := 0;
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);

                    FOR r IN
                        SELECT id, connector_name, sync_cursors, backfill_low_water_marks
                        FROM connector_configurations
                        WHERE connector_name IN ('glooko', 'tandem') AND jsonb_typeof(sync_cursors) = 'object'
                    LOOP
                        vendor := r.sync_cursors;

                        IF r.connector_name = 'glooko' THEN
                            resume := CASE
                                WHEN jsonb_typeof(r.backfill_low_water_marks) = 'object' THEN r.backfill_low_water_marks
                                ELSE '{}'::jsonb
                            END;

                            FOREACH stamp_key IN ARRAY ARRAY['full-walk', 'full-walk-attempt', 'lookback-read'] LOOP
                                CONTINUE WHEN NOT (vendor ? stamp_key);

                                stamped_at := NULL;
                                BEGIN
                                    stamped_at := (vendor -> stamp_key ->> 'LastUpdatedAt')::timestamptz;
                                EXCEPTION WHEN others THEN
                                    stamped_at := NULL;
                                END;

                                vendor := vendor - stamp_key;
                                IF stamped_at IS NULL OR NOT isfinite(stamped_at)
                                   OR extract(year FROM stamped_at AT TIME ZONE 'UTC') NOT BETWEEN 1 AND 9999 THEN
                                    dropped := dropped + 1;
                                ELSE
                                    resume := resume || jsonb_build_object(stamp_key,
                                        to_char(stamped_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
                                END IF;
                            END LOOP;

                            UPDATE connector_configurations
                            SET sync_cursors = vendor,
                                backfill_low_water_marks = NULLIF(resume, '{}'::jsonb)
                            WHERE id = r.id;
                        ELSE
                            FOR profile_key IN
                                SELECT key FROM jsonb_object_keys(r.sync_cursors) AS key WHERE key LIKE 'profile:%'
                            LOOP
                                CONTINUE WHEN jsonb_typeof(vendor -> profile_key) = 'string';

                                IF jsonb_typeof(vendor -> profile_key -> 'LastGuid') = 'string' THEN
                                    vendor := jsonb_set(vendor, ARRAY[profile_key], vendor -> profile_key -> 'LastGuid');
                                ELSE
                                    vendor := vendor - profile_key;
                                    dropped := dropped + 1;
                                END IF;
                            END LOOP;

                            UPDATE connector_configurations SET sync_cursors = vendor WHERE id = r.id;
                        END IF;
                    END LOOP;

                    UPDATE connector_configurations
                    SET secrets = secrets - 'lastFullWalkAt'
                    WHERE connector_name = 'myfitnesspal' AND jsonb_typeof(secrets) = 'object' AND secrets ? 'lastFullWalkAt';

                    UPDATE connector_configurations
                    SET configuration = configuration - 'lastFullWalkAt'
                    WHERE connector_name = 'myfitnesspal' AND jsonb_typeof(configuration) = 'object'
                      AND configuration ? 'lastFullWalkAt';
                END LOOP;

                IF dropped > 0 THEN
                    RAISE NOTICE 'Connector state rekey dropped % value(s) that could not be read', dropped;
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RekeySql);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Not undone: MyFitnessPal's stamp is gone, and an image from before this migration reads
        /// a missing stamp or fingerprint as never recorded, which costs a walk or a repeat profile.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
