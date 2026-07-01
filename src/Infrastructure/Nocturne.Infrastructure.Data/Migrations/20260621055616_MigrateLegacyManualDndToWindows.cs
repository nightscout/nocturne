using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrateLegacyManualDndToWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR 0004 D5: manual DND moves from columns on tenant_alert_settings to a
            // scope=all row in dnd_windows (the single source of truth for one-shot mutes).
            // Convert each tenant's currently-active manual DND into an all-window BEFORE
            // dropping the columns. dnd_windows is RLS-forced, so set the tenant context per
            // iteration (mirrors MigrateApiSecretsToDirectGrants) for the WITH CHECK to pass.
            // The id is a UUIDv7 built inline (PG17 has no uuidv7()); created_at is the
            // original manual start, not migration time — replay's receipt gate
            // (DndWindowSnapshot.WasActiveAt requires created_at <= tick) must reproduce
            // the suppression the live engine applied before the migration ran.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);

                        INSERT INTO dnd_windows (
                            id, tenant_id, scope, started_at, ends_at,
                            cleared_at, cleared_by, source, created_at
                        )
                        SELECT
                            encode(
                                set_bit(
                                    set_bit(
                                        overlay(uuid_send(gen_random_uuid())
                                                placing substring(int8send((extract(epoch from clock_timestamp()) * 1000)::bigint) from 3)
                                                from 1 for 6),
                                        52, 1),
                                    53, 1),
                                'hex')::uuid,
                            s.tenant_id,
                            'all',
                            COALESCE(s.dnd_manual_started_at, now()),
                            s.dnd_manual_until,
                            NULL,
                            NULL,
                            'migrated:legacy-manual',
                            COALESCE(s.dnd_manual_started_at, now())
                        FROM tenant_alert_settings s
                        WHERE s.tenant_id = r.id
                          AND s.dnd_manual_active = true
                          AND (s.dnd_manual_until IS NULL OR s.dnd_manual_until > now());
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "dnd_manual_active",
                table: "tenant_alert_settings");

            migrationBuilder.DropColumn(
                name: "dnd_manual_started_at",
                table: "tenant_alert_settings");

            migrationBuilder.DropColumn(
                name: "dnd_manual_until",
                table: "tenant_alert_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-adds the columns empty — the migrated manual state stays in dnd_windows and is
            // not copied back (the windows remain the source of truth even on a downgrade).
            migrationBuilder.AddColumn<bool>(
                name: "dnd_manual_active",
                table: "tenant_alert_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "dnd_manual_started_at",
                table: "tenant_alert_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "dnd_manual_until",
                table: "tenant_alert_settings",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
