using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Moves the <c>enteredBy</c> the decomposer stored as a temp basal's <c>app</c> into the kept field
    /// <see cref="TempBasalUploadedFields"/> reads it from, and clears <c>app</c>, which now holds only
    /// the uploaded <c>app</c>. Such a row is one with a legacy id whose <c>app</c> equals its
    /// <c>device</c>: the decomposer stored <c>enteredBy</c> in both. A row that already keeps an
    /// <c>enteredBy</c> was written since, so its <c>app</c> is the uploaded one and stays.
    /// </summary>
    /// <remarks>
    /// The served treatment is unchanged, so <c>sys_updated_at</c> is left as it is. Each tenant is
    /// walked a page at a time, committing each page; FORCE ROW LEVEL SECURITY hides a tenant's rows
    /// until its tenant is set, and the setting is local to a transaction, so it is set again after
    /// every <c>COMMIT</c>. A moved row no longer matches, so running it again changes nothing.
    /// </remarks>
    public partial class MoveTempBasalEnteredByOutOfApp : Migration
    {
        internal const int PageSize = 20_000;

        internal static string UpSql { get; } = PagedSql("""
            UPDATE temp_basals
            SET additional_properties = COALESCE(additional_properties, '{}'::jsonb)
                    || jsonb_build_object('enteredBy', app),
                app = NULL
            WHERE tenant_id = t.id
              AND (sys_updated_at, id) > (after_at, after_id) AND (sys_updated_at, id) <= (last_at, last_id)
              AND legacy_id IS NOT NULL AND app = device
              AND additional_properties -> 'enteredBy' IS NULL;
            """);

        /// <summary>
        /// Gives every decomposer row the <c>app</c> the code before this migration wrote: the kept
        /// <c>enteredBy</c>, or none.
        /// </summary>
        internal static string DownSql { get; } = PagedSql("""
            UPDATE temp_basals
            SET app = additional_properties ->> 'enteredBy',
                additional_properties = CASE
                    WHEN additional_properties ? 'enteredBy'
                        THEN NULLIF(additional_properties - 'enteredBy', '{}'::jsonb)
                    ELSE additional_properties
                END
            WHERE tenant_id = t.id
              AND (sys_updated_at, id) > (after_at, after_id) AND (sys_updated_at, id) <= (last_at, last_id)
              AND legacy_id IS NOT NULL
              AND (app IS NOT NULL OR additional_properties -> 'enteredBy' IS NOT NULL);
            """);

        /// <summary>Pages on <c>ix_temp_basals_tenant_history</c>, whose key the update leaves alone.</summary>
        private static string PagedSql(string update) => $$"""
            DO $$
            DECLARE
                t RECORD;
                after_at timestamptz;
                after_id uuid;
                last_at timestamptz;
                last_id uuid;
            BEGIN
                FOR t IN SELECT id FROM tenants ORDER BY id LOOP
                    after_at := '-infinity';
                    after_id := '00000000-0000-0000-0000-000000000000';
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        last_at := NULL;
                        last_id := NULL;
                        SELECT sys_updated_at, id INTO last_at, last_id
                        FROM (
                            SELECT sys_updated_at, id FROM temp_basals
                            WHERE tenant_id = t.id AND (sys_updated_at, id) > (after_at, after_id)
                            ORDER BY sys_updated_at, id
                            LIMIT {{PageSize}}) page
                        ORDER BY sys_updated_at DESC, id DESC
                        LIMIT 1;
                        EXIT WHEN last_id IS NULL;

                        {{update}}

                        after_at := last_at;
                        after_id := last_id;
                        COMMIT;
                    END LOOP;
                END LOOP;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql, suppressTransaction: true);
        }
    }
}
