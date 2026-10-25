using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Deletes the Profile spans <c>AddProfileActivations</c> converted and left in place, with their
    /// <c>linked_records</c> rows, and the <c>@@@@@</c> settings and schedule rows a Profile Switch's
    /// <c>profileJson</c> was decomposed into. A span a pre-cutover image wrote after that migration ran
    /// is converted first (<see cref="AddProfileActivations.ConvertProfileSpans"/>); only a span an
    /// activation holds the id of is deleted, and any other is reported and kept. A snapshot row is
    /// matched by the shape the switch decomposition wrote: <c>{name}@@@@@{mills}</c> under the legacy id
    /// <c>{switch id}:{name}</c>, where the switch id is an activation's key, so a profile document
    /// whose store name merely contains <c>@@@@@</c> stays.
    /// <para>
    /// Each converted span's id is now its activation's, so the rows are deleted rather than
    /// soft-deleted: a tombstone under that id is a delete a history read could deliver for a record
    /// still served, and a tombstoned <c>@@@@@</c> settings row would reach v3 profile history as a
    /// deleted document. FORCE ROW LEVEL SECURITY binds the migrator, hence the per-tenant GUC; each
    /// statement names the tenant too, so a role that bypasses RLS still deletes each tenant's rows once.
    /// </para>
    /// </summary>
    public partial class RemoveLegacyProfileSpansAndSwitchSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddProfileActivations.ConvertProfileSpans(unconvertedOnly: true));

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    kept integer;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        DELETE FROM linked_records l
                        USING state_spans s
                        WHERE l.tenant_id = t.id
                          AND s.tenant_id = t.id
                          AND s.category = 'Profile'
                          AND l.record_type = 'statespan'
                          AND l.record_id = s.id
                          AND EXISTS (SELECT 1 FROM profile_activations a WHERE a.tenant_id = t.id AND a.id = s.id);

                        DELETE FROM state_spans s
                        WHERE s.tenant_id = t.id
                          AND s.category = 'Profile'
                          AND EXISTS (SELECT 1 FROM profile_activations a WHERE a.tenant_id = t.id AND a.id = s.id);

                        SELECT count(*) INTO kept FROM state_spans s
                        WHERE s.tenant_id = t.id AND s.category = 'Profile';
                        IF kept > 0 THEN
                            RAISE WARNING 'Tenant % keeps % Profile span(s) no activation holds', t.id, kept;
                        END IF;

                        DELETE FROM therapy_settings p
                        WHERE p.tenant_id = t.id
                          AND p.profile_name ~ '@@@@@[0-9]+$'
                          AND EXISTS (
                              SELECT 1 FROM profile_activations a
                              WHERE a.tenant_id = t.id
                                AND (p.legacy_id = a.legacy_id || ':' || p.profile_name
                                     OR p.legacy_id = a.sync_identifier || ':' || p.profile_name));

                        DELETE FROM basal_schedules p
                        WHERE p.tenant_id = t.id
                          AND p.profile_name ~ '@@@@@[0-9]+$'
                          AND EXISTS (
                              SELECT 1 FROM profile_activations a
                              WHERE a.tenant_id = t.id
                                AND (p.legacy_id = a.legacy_id || ':' || p.profile_name
                                     OR p.legacy_id = a.sync_identifier || ':' || p.profile_name));

                        DELETE FROM carb_ratio_schedules p
                        WHERE p.tenant_id = t.id
                          AND p.profile_name ~ '@@@@@[0-9]+$'
                          AND EXISTS (
                              SELECT 1 FROM profile_activations a
                              WHERE a.tenant_id = t.id
                                AND (p.legacy_id = a.legacy_id || ':' || p.profile_name
                                     OR p.legacy_id = a.sync_identifier || ':' || p.profile_name));

                        DELETE FROM sensitivity_schedules p
                        WHERE p.tenant_id = t.id
                          AND p.profile_name ~ '@@@@@[0-9]+$'
                          AND EXISTS (
                              SELECT 1 FROM profile_activations a
                              WHERE a.tenant_id = t.id
                                AND (p.legacy_id = a.legacy_id || ':' || p.profile_name
                                     OR p.legacy_id = a.sync_identifier || ':' || p.profile_name));

                        DELETE FROM target_range_schedules p
                        WHERE p.tenant_id = t.id
                          AND p.profile_name ~ '@@@@@[0-9]+$'
                          AND EXISTS (
                              SELECT 1 FROM profile_activations a
                              WHERE a.tenant_id = t.id
                                AND (p.legacy_id = a.legacy_id || ':' || p.profile_name
                                     OR p.legacy_id = a.sync_identifier || ':' || p.profile_name));
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        /// <remarks>The deleted rows cannot be restored; their profile switches live on as activations.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
