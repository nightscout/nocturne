using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Moves the profile versions decomposed from a document posted without <c>mills</c> from the epoch,
    /// where <c>ProfileDecomposer</c> put them before it read <c>startDate</c> (#2377), to the
    /// <c>startDate</c> their therapy settings row recorded, and leaves a
    /// <c>pending_tenant_jobs</c> row for each tenant it moved anything for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A live document row (a legacy id, not a profile-switch snapshot) at the epoch moves with its four
    /// schedules: the live rows of the same legacy id that are still at the epoch. A store's five rows
    /// keep one <c>timestamp</c> and take one <c>sys_updated_at</c>, so the same-batch witness of the
    /// mmol repairs holds whichever of them runs first, and a v3 history client fetches the moved
    /// document. Only an ISO 8601 <c>start_date</c> is read, a zone-less one as UTC as the decomposer
    /// reads it; anything else, the epoch itself included, is left at the epoch and named in a notice,
    /// and a re-upload of its document places it. The document's <c>created_at</c> was never stored.
    /// </para>
    /// <para>
    /// Every upload at the epoch claimed the default, so it sits on the last document uploaded. For a
    /// tenant it moved rows for whose default is not on the newest document (newest by start, then
    /// document id), the default goes to that document's store of the same name, which keeps a name a
    /// user chose through V4 as <c>ProfileStoreTimeline</c> reads it; failing that, to the store the
    /// document names (<c>document_default_profile</c>), or to none when that names none of its
    /// stores, as the decomposer settles it; failing both, it stays where it is.
    /// </para>
    /// <para>
    /// The percent temp basal rates resolved against the moved versions, and the tenant's cached
    /// reads, are redone after startup by <c>TempBasalRateSweepService</c>, which takes the
    /// <c>pending_tenant_jobs</c> row. A moved row is no longer selected, so running this again
    /// changes nothing. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC.
    /// </para>
    /// </remarks>
    public partial class RepairEpochProfileVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_tenant_jobs",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_tenant_jobs", x => new { x.tenant_id, x.job });
                    table.ForeignKey(
                        name: "FK_pending_tenant_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(RepairSql);
        }

        /// <inheritdoc />
        /// <remarks>The timestamps and flags it replaced were wrong, so they are not restored.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_tenant_jobs");
        }

        private const string RepairSql = """
            DO $$
            DECLARE
                epoch CONSTANT timestamptz := 'epoch';
                iso CONSTANT text := '^\s*\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?)?\s*(Z|[+-]\d{2}(:?\d{2})?)?\s*$';
                t RECORD;
                r RECORD;
                start_at timestamptz;
                reason text;
                newest_document text;
                newest_default text;
                current_document text;
                current_name text;
                target uuid;
                n_rows int;
                n_stores int;
                n_schedules int;
                n_left int;
            BEGIN
                PERFORM set_config('timezone', 'UTC', true);

                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);
                    n_stores := 0; n_schedules := 0; n_left := 0;

                    FOR r IN
                        SELECT ts.id, ts.legacy_id, ts.start_date
                        FROM therapy_settings ts
                        WHERE ts.tenant_id = t.id
                          AND ts.deleted_at IS NULL
                          AND ts.timestamp = epoch
                          AND ts.legacy_id IS NOT NULL
                          AND strpos(ts.profile_name, '@@@@@') = 0
                    LOOP
                        start_at := NULL;
                        reason := NULL;
                        IF r.start_date IS NULL OR r.start_date !~ iso THEN
                            reason := format('no ISO 8601 startDate (%s)', coalesce(r.start_date, 'none'));
                        ELSE
                            BEGIN
                                start_at := r.start_date::timestamptz;
                            EXCEPTION WHEN data_exception THEN
                                reason := format('its startDate %s is not a date', r.start_date);
                            END;
                            IF reason IS NULL AND NOT isfinite(start_at) THEN
                                reason := format('its startDate %s is not a finite date', r.start_date);
                            ELSIF reason IS NULL AND start_at = epoch THEN
                                reason := 'its startDate is the epoch';
                            END IF;
                        END IF;

                        IF reason IS NOT NULL THEN
                            n_left := n_left + 1;
                            RAISE NOTICE 'tenant %: therapy_settings % (%) left at the epoch: %', t.id, r.id, r.legacy_id, reason;
                            CONTINUE;
                        END IF;

                        UPDATE therapy_settings SET timestamp = start_at, sys_updated_at = now()
                        WHERE id = r.id AND tenant_id = t.id;

                        UPDATE basal_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        UPDATE carb_ratio_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        UPDATE sensitivity_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        UPDATE target_range_schedules SET timestamp = start_at, sys_updated_at = now()
                        WHERE tenant_id = t.id AND legacy_id = r.legacy_id AND deleted_at IS NULL AND timestamp = epoch;
                        GET DIAGNOSTICS n_rows = ROW_COUNT;
                        n_schedules := n_schedules + n_rows;

                        n_stores := n_stores + 1;
                    END LOOP;

                    IF n_stores + n_left > 0 THEN
                        RAISE NOTICE 'tenant %: moved % profile stores and % of their schedules off the epoch to their startDate; left % at the epoch',
                            t.id, n_stores, n_schedules, n_left;
                    END IF;

                    IF n_stores > 0 THEN
                        INSERT INTO pending_tenant_jobs (tenant_id, job, requested_at)
                        VALUES (t.id, 'profile-versions-moved', now());

                        SELECT d.document INTO newest_document
                        FROM (SELECT CASE
                                   WHEN ts.legacy_id IS NULL THEN ts.id::text
                                   WHEN strpos(ts.legacy_id, ':') > 0 THEN split_part(ts.legacy_id, ':', 1)
                                   ELSE ts.legacy_id
                               END AS document, ts.timestamp
                              FROM therapy_settings ts
                              WHERE ts.tenant_id = t.id AND ts.deleted_at IS NULL AND strpos(ts.profile_name, '@@@@@') = 0) d
                        ORDER BY d.timestamp DESC, d.document COLLATE "C" DESC
                        LIMIT 1;

                        SELECT CASE
                                   WHEN ts.legacy_id IS NULL THEN ts.id::text
                                   WHEN strpos(ts.legacy_id, ':') > 0 THEN split_part(ts.legacy_id, ':', 1)
                                   ELSE ts.legacy_id
                               END, ts.profile_name
                        INTO current_document, current_name
                        FROM therapy_settings ts
                        WHERE ts.tenant_id = t.id AND ts.deleted_at IS NULL AND ts.is_default
                        LIMIT 1;

                        IF current_document IS DISTINCT FROM newest_document THEN
                            target := NULL;
                            newest_default := NULL;
                            IF current_name IS NOT NULL THEN
                                SELECT ts.id INTO target
                                FROM therapy_settings ts
                                WHERE ts.tenant_id = t.id AND ts.deleted_at IS NULL
                                  AND ts.legacy_id = newest_document || ':' || current_name;
                            END IF;

                            IF target IS NOT NULL THEN
                                newest_default := current_name;
                            ELSE
                                SELECT max(ts.document_default_profile) INTO newest_default
                                FROM therapy_settings ts
                                WHERE ts.tenant_id = t.id AND ts.deleted_at IS NULL
                                  AND left(ts.legacy_id, length(newest_document) + 1) = newest_document || ':';

                                IF newest_default IS NOT NULL THEN
                                    SELECT ts.id INTO target
                                    FROM therapy_settings ts
                                    WHERE ts.tenant_id = t.id AND ts.deleted_at IS NULL
                                      AND ts.legacy_id = newest_document || ':' || newest_default;
                                END IF;
                            END IF;

                            IF newest_default IS NULL THEN
                                RAISE NOTICE 'tenant %: default profile left on document %: the newest document % holds no store of its name and records no default store',
                                    t.id, coalesce(current_document, 'none'), newest_document;
                            ELSE
                                UPDATE therapy_settings
                                SET is_default = (target IS NOT NULL AND id = target), sys_updated_at = now()
                                WHERE tenant_id = t.id
                                  AND deleted_at IS NULL
                                  AND is_default IS DISTINCT FROM (target IS NOT NULL AND id = target);

                                RAISE NOTICE 'tenant %: default profile moved to store % of the newest document %',
                                    t.id, CASE WHEN target IS NULL THEN 'none' ELSE newest_default END, newest_document;
                            END IF;
                        END IF;
                    END IF;
                END LOOP;
            END $$;
            """;
    }
}
