using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Soft-deletes, as a system delete, every live state span whose category is not a current
    /// <c>StateSpanCategory</c> name: the rows written as <c>TempBasal</c> or <c>BasalDelivery</c>
    /// before those members were removed. A retired span blocks no re-creation, whoever deleted it
    /// (<c>WhereBlocksSpanRecreation</c>), and <c>updated_at</c> is left alone so the retirement does
    /// not read as an edit. Rows already deleted are not touched. The current names are listed
    /// literally because a migration must not follow later changes to the enum. <c>Down</c> leaves the
    /// spans retired: no stored category maps them back, so bringing them back would put rows every
    /// state span read fails on.
    /// </summary>
    public partial class RetireStateSpansOutsideCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);
                        UPDATE state_spans SET deleted_at = now(), deleted_by_user = false
                        WHERE tenant_id = r.id
                          AND deleted_at IS NULL
                          AND category NOT IN ('PumpMode', 'PumpConnectivity', 'Override', 'Profile', 'Exercise',
                                               'Illness', 'Travel', 'DataExclusion', 'TemporaryTarget', 'Motion');
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
