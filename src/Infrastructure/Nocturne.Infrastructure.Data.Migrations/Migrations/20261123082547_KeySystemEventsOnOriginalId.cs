using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Makes <c>original_id</c> the unique key of <c>system_events</c> within a tenant, as
    /// <c>SystemEventRepository</c> already treats it: a non-empty id names one event. An empty id
    /// is stored as null, like no id, so the repository and the mapper agree and the index filter is
    /// the plain <c>original_id IS NOT NULL</c>, which the planner can prove from any
    /// <c>original_id = ...</c> lookup. Every loser of a duplicate key is deleted first; the table
    /// has no soft delete and nothing references its rows. The winner is the row with the newest
    /// <c>created_at</c>, then the highest id, because the table records no update time and the row
    /// written last holds the values the latest sync offered.
    /// </summary>
    public partial class KeySystemEventsOnOriginalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE system_events SET original_id = NULL
                        WHERE tenant_id = t.id AND original_id = '';

                        DELETE FROM system_events s
                        USING (
                            SELECT id
                            FROM (
                                SELECT id,
                                       row_number() OVER (
                                           PARTITION BY tenant_id, original_id
                                           ORDER BY created_at DESC, id DESC) AS rn
                                FROM system_events
                                WHERE tenant_id = t.id AND original_id IS NOT NULL
                            ) ranked
                            WHERE ranked.rn > 1
                        ) losers
                        WHERE s.tenant_id = t.id AND s.id = losers.id;
                    END LOOP;
                END $$;
                """);

            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_system_events_tenant_original_id",
                "ON system_events (tenant_id, original_id) WHERE original_id IS NOT NULL");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_system_events_original_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(migrationBuilder, "ix_system_events_original_id", "ON system_events (original_id)");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_system_events_tenant_original_id");
        }
    }
}
