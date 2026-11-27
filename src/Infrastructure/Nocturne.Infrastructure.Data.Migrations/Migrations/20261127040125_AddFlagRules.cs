using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlagRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flag_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dataset = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    days = table.Column<int>(type: "integer", nullable: true),
                    comparison = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flag_rules", x => x.id);
                    table.CheckConstraint("ck_flag_rules_comparison", "comparison IN ('below', 'above')");
                    table.CheckConstraint("ck_flag_rules_days", "days IS NULL OR days >= 1");
                    table.CheckConstraint("ck_flag_rules_level", "level IN ('info', 'warn', 'hazard')");
                    table.CheckConstraint("ck_flag_rules_one_owner", "(subject_id IS NULL) <> (team_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_flag_rules_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flag_rules_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flag_rules_subject_id",
                table: "flag_rules",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_flag_rules_team_id",
                table: "flag_rules",
                column: "team_id");

            // Strict, unlike the subject_pin policies on subjects and refresh_tokens: a context with no subject
            // pinned (every tenant and platform context) reads and writes no rule at all.
            const string subjectPin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";
            migrationBuilder.Sql("ALTER TABLE flag_rules ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE flag_rules FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY subject_pin ON flag_rules
                    USING (subject_id = {subjectPin})
                    WITH CHECK (subject_id = {subjectPin});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flag_rules");
        }
    }
}
