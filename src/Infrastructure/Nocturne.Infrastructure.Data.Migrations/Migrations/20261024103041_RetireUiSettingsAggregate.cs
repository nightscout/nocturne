using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetireUiSettingsAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Earlier versions stored the whole UI settings document in ui:settings:complete, and
            // reads fell back to it for any section without a row of its own. Each section it holds
            // moves to the row UISettingsService reads, unless that row already serves a value (an
            // active row whose value is neither SQL nor JSON null), less the properties that row
            // never carries; the alarm configuration moves to its own row only when neither the
            // notifications row nor the alarms row served one. Then the aggregate is deleted.
            //
            // A tenant's move runs in its own block, so a document Postgres cannot convert never
            // aborts the migration and with it API startup. Postgres refuses a \u0000 escape that
            // System.Text.Json reads, so each becomes � first: the same length, so the JSON
            // stays valid even where the six characters follow an escaped backslash. An aggregate
            // that is not JSON fails every read today and is dropped; one still unconvertible (a
            // non-ASCII escape on a database not encoded in UTF8) is left in place with a warning.
            // FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    raw text;
                    doc jsonb;
                    section text;
                    part jsonb;
                    alarms jsonb;
                    target text;
                    stamp bigint := (extract(epoch FROM now()) * 1000)::bigint;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        raw := NULL;
                        SELECT value INTO raw
                        FROM settings
                        WHERE tenant_id = t.id AND key = 'ui:settings:complete' AND is_active;

                        BEGIN
                            doc := replace(raw, '\u0000', '�')::jsonb;

                            IF jsonb_typeof(doc) = 'object' THEN
                                FOREACH section IN ARRAY ARRAY['devices', 'algorithm', 'features', 'notifications', 'services', 'dataQuality'] LOOP
                                    part := doc -> section;
                                    CONTINUE WHEN jsonb_typeof(part) IS DISTINCT FROM 'object';
                                    CONTINUE WHEN EXISTS (
                                        SELECT 1 FROM settings
                                        WHERE tenant_id = t.id AND key = 'ui:settings:' || lower(section)
                                          AND is_active AND value IS NOT NULL AND btrim(value, E' \t\r\n') <> 'null');

                                    IF section = 'notifications' THEN
                                        alarms := part -> 'alarmConfiguration';
                                        IF jsonb_typeof(alarms) = 'object' AND NOT EXISTS (
                                            SELECT 1 FROM settings
                                            WHERE tenant_id = t.id AND key = 'ui:settings:notifications:alarms'
                                              AND is_active AND value IS NOT NULL AND btrim(value, E' \t\r\n') <> 'null') THEN
                                            UPDATE settings
                                            SET value = alarms::text, mills = stamp, srv_modified = now(), is_active = true
                                            WHERE tenant_id = t.id AND key = 'ui:settings:notifications:alarms';
                                            IF NOT FOUND THEN
                                                INSERT INTO settings (id, tenant_id, key, value, mills, srv_created, srv_modified, is_active, notes, app)
                                                VALUES (gen_random_uuid(), t.id, 'ui:settings:notifications:alarms', alarms::text,
                                                        stamp, now(), now(), true, 'xDrip+-style alarm profiles configuration', 'nocturne-api');
                                            END IF;
                                        END IF;
                                        part := part - 'alarmConfiguration';
                                    ELSIF section = 'services' THEN
                                        part := part - 'availableServices';
                                    END IF;

                                    target := 'ui:settings:' || lower(section);
                                    UPDATE settings
                                    SET value = part::text, mills = stamp, srv_modified = now(), is_active = true
                                    WHERE tenant_id = t.id AND key = target;
                                    IF NOT FOUND THEN
                                        INSERT INTO settings (id, tenant_id, key, value, mills, srv_created, srv_modified, is_active, notes, app)
                                        VALUES (gen_random_uuid(), t.id, target, part::text,
                                                stamp, now(), now(), true, 'UI settings section: ' || section, 'nocturne-api');
                                    END IF;
                                END LOOP;
                            END IF;

                            DELETE FROM settings WHERE tenant_id = t.id AND key = 'ui:settings:complete';
                        EXCEPTION
                            WHEN invalid_text_representation THEN
                                DELETE FROM settings WHERE tenant_id = t.id AND key = 'ui:settings:complete';
                            WHEN untranslatable_character THEN
                                RAISE WARNING 'ui:settings:complete of tenant % cannot be converted on this database and is left in place', t.id;
                        END;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
