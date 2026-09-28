using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettleSingleDefaultTherapySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ingest used to flag each profile document's own default store, so a tenant fed by
            // several documents holds several defaults. Keep the newest, the store the newest
            // document named. Profile-switch snapshots ('@@@@@' stores) were flagged too but are
            // not documents, so they are cleared first and never win. FORCE ROW LEVEL SECURITY
            // binds the migrator, hence the per-tenant GUC loop; each statement still names the
            // tenant for a migrator that bypasses RLS.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE therapy_settings
                        SET is_default = false
                        WHERE tenant_id = t.id
                          AND is_default
                          AND strpos(profile_name, '@@@@@') > 0;

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY tenant_id
                                                      ORDER BY timestamp DESC, id DESC) AS rn
                            FROM therapy_settings
                            WHERE tenant_id = t.id
                              AND is_default
                              AND deleted_at IS NULL)
                        UPDATE therapy_settings s
                        SET is_default = false
                        FROM ranked
                        WHERE ranked.id = s.id AND ranked.rn > 1 AND s.tenant_id = t.id;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Which extra rows were flagged is not recorded, and restoring them would restore the bug.
        }
    }
}
