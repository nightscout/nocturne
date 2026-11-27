using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "people_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, computedColumnSql: "lower(name)", stored: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    query = table.Column<string>(type: "jsonb", nullable: false),
                    lens = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    review_days = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people_views", x => x.id);
                    table.CheckConstraint("ck_people_views_lens", "(lens = 'now' AND review_days IS NULL) OR (lens = 'review' AND review_days IS NOT NULL AND review_days IN (14, 30, 90))");
                    table.CheckConstraint("ck_people_views_position", "position >= 0");
                    table.ForeignKey(
                        name: "FK_people_views_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_people_views_subject_id_name_key",
                table: "people_views",
                columns: new[] { "subject_id", "name_key" },
                unique: true);

            // Strict, as on flag_rules: a context with no subject pinned (every tenant and platform context) reads and
            // writes no view at all.
            const string subjectPin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";
            migrationBuilder.Sql("ALTER TABLE people_views ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE people_views FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY subject_pin ON people_views
                    USING (subject_id = {subjectPin})
                    WITH CHECK (subject_id = {subjectPin});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "people_views");
        }
    }
}
