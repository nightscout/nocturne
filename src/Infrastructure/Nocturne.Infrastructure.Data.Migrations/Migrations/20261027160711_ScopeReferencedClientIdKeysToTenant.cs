using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Widens the primary key of <c>foods</c>, <c>state_spans</c> and <c>sleep_sessions</c> to
    /// <c>(tenant_id, id)</c>, as <c>20261027134107_ScopeClientIdKeysToTenant</c> did for the tables
    /// without inbound references, and makes every reference to them a composite foreign key
    /// <c>(tenant_id, x_id)</c>. Every key is built with <see cref="ConcurrentIndexBuilder"/> first.
    /// Each parent is then swapped in one short statement that also adds its foreign keys
    /// <c>NOT VALID</c>, so the write locks are held only for catalogue changes. Each foreign key is
    /// validated afterwards, which does not block writes.
    /// <para>
    /// Each step checks the state it changes, so an <c>Up</c> interrupted at any point completes on
    /// a retry. A retry after a parent's swap builds that parent's key index again, since the swap
    /// renamed the first one, and drops it once it finds the swap done.
    /// </para>
    /// <para>
    /// A row could name a parent in another tenant, since a foreign key check ignores row level
    /// security and a state span's <c>SupersededById</c> comes from the client. Its own tenant cannot
    /// see that parent, and the composite key would refuse it. Before validating, a nullable
    /// reference to another tenant's row is set to null, and a row whose required parent is in
    /// another tenant (a sleep stage, a biometric sample, a favourite) is deleted. Each count is
    /// raised as a notice. The cleanup runs in its own transaction after the foreign keys exist, so
    /// no new such row can appear behind it.
    /// </para>
    /// </summary>
    /// <remarks><c>Down</c> fails once two tenants hold the same id.</remarks>
    public partial class ScopeReferencedClientIdKeysToTenant : Migration
    {
        private sealed record Reference(string Table, string Column, string Parent, bool Required, string Index)
        {
            /// <summary>EF's name for the foreign key, cut to PostgreSQL's 63 characters as EF cuts it.</summary>
            public string ForeignKey => $"FK_{Table}_{Parent}_tenant_id_{Column}" switch
            {
                { Length: > 63 } name => name[..62] + "~",
                var name => name,
            };

            public string ObsoleteForeignKey => $"FK_{Table}_{Parent}_{Column}";
        }

        private static readonly string[] Parents = ["foods", "state_spans", "sleep_sessions"];

        private static readonly Reference[] References =
        [
            new("treatment_foods", "food_id", "foods", false, "ix_treatment_foods_tenant_id_food_id"),
            new("connector_food_entries", "food_id", "foods", false, "\"IX_connector_food_entries_tenant_id_food_id\""),
            new("user_food_favorites", "food_id", "foods", true, "ix_user_food_favorites_tenant_id_food_id"),
            new("state_spans", "superseded_by_id", "state_spans", false, "ix_state_spans_tenant_id_superseded_by_id"),
            new("sleep_stages", "sleep_session_id", "sleep_sessions", true, "ix_sleep_stages_tenant_id_sleep_session_id"),
            new("sleep_biometric_samples", "sleep_session_id", "sleep_sessions", true, "ix_sleep_biometric_samples_tenant_id_sleep_session_id"),
        ];

        /// <summary>
        /// The single-column indexes the composite ones replace, each its leading <c>tenant_id</c>
        /// or its referencing column.
        /// </summary>
        private static readonly (string Name, string Table, string Column)[] ObsoleteIndexes =
        [
            ("ix_treatment_foods_food_id", "treatment_foods", "food_id"),
            ("IX_treatment_foods_tenant_id", "treatment_foods", "tenant_id"),
            ("IX_connector_food_entries_food_id", "connector_food_entries", "food_id"),
            ("ix_user_food_favorites_food_id", "user_food_favorites", "food_id"),
            ("ix_state_spans_superseded_by_id", "state_spans", "superseded_by_id"),
            ("ix_sleep_stages_sleep_session_id", "sleep_stages", "sleep_session_id"),
            ("IX_sleep_stages_tenant_id", "sleep_stages", "tenant_id"),
            ("ix_sleep_biometric_samples_sleep_session_id", "sleep_biometric_samples", "sleep_session_id"),
            ("IX_sleep_biometric_samples_tenant_id", "sleep_biometric_samples", "tenant_id"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var parent in Parents)
            {
                ConcurrentIndexBuilder.BuildUnique(
                    migrationBuilder, $"pk_{parent}_tenant_id_id", $"ON {parent} (tenant_id, id)");
            }

            foreach (var reference in References)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder, reference.Index, $"ON {reference.Table} (tenant_id, {reference.Column})");
            }

            foreach (var parent in Parents)
            {
                var inbound = References.Where(r => r.Parent == parent).ToArray();

                migrationBuilder.Sql(string.Join("\n", [
                    "DO $$",
                    "BEGIN",
                    .. inbound.Select(r => $"ALTER TABLE {r.Table} DROP CONSTRAINT IF EXISTS \"{r.ObsoleteForeignKey}\";"),
                    $"""
                    IF (SELECT array_length(conkey, 1) FROM pg_constraint
                        WHERE conrelid = 'public.{parent}'::regclass AND contype = 'p') = 1
                    THEN
                        ALTER TABLE {parent}
                            DROP CONSTRAINT "PK_{parent}",
                            ADD CONSTRAINT "PK_{parent}" PRIMARY KEY USING INDEX pk_{parent}_tenant_id_id;
                    END IF;
                    """,
                    .. inbound.Select(r => $"""
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint
                            WHERE conrelid = 'public.{r.Table}'::regclass AND conname = '{r.ForeignKey}')
                        THEN
                            ALTER TABLE {r.Table} ADD CONSTRAINT "{r.ForeignKey}"
                                FOREIGN KEY (tenant_id, {r.Column}) REFERENCES {parent} (tenant_id, id)
                                ON DELETE {(r.Required ? "CASCADE" : $"SET NULL ({r.Column})")} NOT VALID;
                        END IF;
                        """),
                    "END $$;",
                ]), suppressTransaction: true);
            }

            foreach (var parent in Parents)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"pk_{parent}_tenant_id_id");
            }

            migrationBuilder.Sql(CrossTenantReferenceCleanup(), suppressTransaction: true);

            foreach (var reference in References)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE {reference.Table} VALIDATE CONSTRAINT \"{reference.ForeignKey}\";",
                    suppressTransaction: true);
            }

            foreach (var index in ObsoleteIndexes)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"\"{index.Name}\"");
            }
        }

        /// <summary>
        /// Clears or deletes, tenant by tenant, every reference whose parent is not in the row's own
        /// tenant. FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC; under it
        /// the parent lookup sees only that tenant's rows.
        /// </summary>
        private static string CrossTenantReferenceCleanup()
        {
            var totals = string.Join("\n", References.Select((_, i) => $"    total_{i} bigint := 0;"));

            var statements = string.Join("\n", References.Select((r, i) => $"""
                        {(r.Required ? $"DELETE FROM {r.Table} c" : $"UPDATE {r.Table} c SET {r.Column} = NULL")}
                        WHERE c.{r.Column} IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM {r.Parent} p WHERE p.tenant_id = c.tenant_id AND p.id = c.{r.Column});
                        GET DIAGNOSTICS n = ROW_COUNT;
                        total_{i} := total_{i} + n;
                """));

            var notices = string.Join("\n", References.Select((r, i) =>
                $"    RAISE NOTICE '{(r.Required ? "deleted" : "cleared")} % {r.Table} rows whose {r.Column} named another tenant''s row', total_{i};"));

            return $"""
                DO $$
                DECLARE
                    t RECORD;
                    n bigint;
                {totals}
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);
                {statements}
                    END LOOP;
                {notices}
                END $$;
                """;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var reference in References)
            {
                migrationBuilder.DropForeignKey(name: reference.ForeignKey, table: reference.Table);
            }

            foreach (var parent in Parents)
            {
                migrationBuilder.DropPrimaryKey(name: $"PK_{parent}", table: parent);
                migrationBuilder.AddPrimaryKey(name: $"PK_{parent}", table: parent, column: "id");
            }

            foreach (var reference in References)
            {
                migrationBuilder.AddForeignKey(
                    name: reference.ObsoleteForeignKey,
                    table: reference.Table,
                    column: reference.Column,
                    principalTable: reference.Parent,
                    principalColumn: "id",
                    onDelete: reference.Required ? ReferentialAction.Cascade : ReferentialAction.SetNull);
            }

            foreach (var index in ObsoleteIndexes)
            {
                migrationBuilder.CreateIndex(name: index.Name, table: index.Table, column: index.Column);
            }

            foreach (var reference in References)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, reference.Index);
            }
        }
    }
}
