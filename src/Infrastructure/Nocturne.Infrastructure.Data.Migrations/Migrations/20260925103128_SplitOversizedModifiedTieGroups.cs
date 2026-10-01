using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitOversizedModifiedTieGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Splits existing tenant millisecond groups wider than
            // NocturneDbContext.SystemTimestampGroupSize the way a save now stamps them: row k moves
            // floor(k/1000) ms on. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant
            // GUC loop.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM boluses)
                        UPDATE boluses b
                        SET sys_updated_at = b.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = b.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM carb_intakes)
                        UPDATE carb_intakes c
                        SET sys_updated_at = c.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = c.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM bg_checks)
                        UPDATE bg_checks b
                        SET sys_updated_at = b.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = b.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM notes)
                        UPDATE notes n
                        SET sys_updated_at = n.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = n.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM device_events)
                        UPDATE device_events d
                        SET sys_updated_at = d.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = d.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM temp_basals)
                        UPDATE temp_basals tb
                        SET sys_updated_at = tb.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = tb.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM bolus_calculations)
                        UPDATE bolus_calculations b
                        SET sys_updated_at = b.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = b.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH ranked AS (
                            SELECT id,
                                   row_number() OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)
                                                      ORDER BY sys_updated_at, id) - 1 AS k,
                                   count(*) OVER (PARTITION BY date_trunc('milliseconds', sys_updated_at)) AS n
                            FROM aps_snapshots)
                        UPDATE aps_snapshots a
                        SET sys_updated_at = a.sys_updated_at + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = a.id AND ranked.n > 1000 AND ranked.k >= 1000;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The shift cannot be undone, and losing it only risks the oversized tie group it removed.
        }
    }
}
