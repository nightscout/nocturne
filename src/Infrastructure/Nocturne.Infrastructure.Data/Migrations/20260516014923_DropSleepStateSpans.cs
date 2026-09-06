using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSleepStateSpans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sleep now lives in the dedicated sleep_sessions tables. The legacy
            // StateSpan rows held only a coarse start/end/state and cannot be
            // faithfully reshaped into stage-aware sessions, so they are dropped
            // rather than migrated. Sources re-sync sleep into the new tables.
            // state_spans has FORCE ROW LEVEL SECURITY and the migrator role is
            // NOBYPASSRLS, so state_spans cannot be used to discover tenants —
            // without app.current_tenant_id set, RLS filters every row. Tenants
            // are enumerated from the tenants table (not tenant-scoped, no RLS
            // policy), and the GUC is set per iteration so each DELETE runs
            // inside that tenant's RLS context.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);
                        DELETE FROM state_spans WHERE category = 'Sleep' AND tenant_id = r.id;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way data migration — dropped Sleep StateSpans cannot be restored.
        }
    }
}
