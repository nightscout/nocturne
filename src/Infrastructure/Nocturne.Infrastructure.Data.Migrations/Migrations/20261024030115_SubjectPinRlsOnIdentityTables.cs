using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubjectPinRlsOnIdentityTables : Migration
    {
        /// <summary>
        /// The identity tables a subject-pinned context reads, each with the column naming the one
        /// subject a row belongs to. With <c>app.current_subject_id</c> unset or empty every row is
        /// visible, so only a subject pin is narrowed; the migrator, seeders and every other context
        /// never set it. A permissive policy added to these tables later is ORed with this one, whose
        /// unset arm admits every row, so a policy meant to narrow them must be RESTRICTIVE.
        /// </summary>
        private static readonly (string Table, string SubjectColumn)[] Tables =
        [
            ("subjects", "id"),
            ("tenant_members", "subject_id"),
            ("refresh_tokens", "subject_id"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Tables)
            {
                var pinned = $"""
                    NULLIF(current_setting('app.current_subject_id', true), '') IS NULL
                    OR {column} = NULLIF(current_setting('app.current_subject_id', true), '')::uuid
                    """;

                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY subject_pin ON {table}
                        USING ({pinned})
                        WITH CHECK ({pinned});
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, _) in Tables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS subject_pin ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
