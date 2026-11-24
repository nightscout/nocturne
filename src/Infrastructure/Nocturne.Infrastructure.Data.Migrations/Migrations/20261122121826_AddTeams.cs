using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "restrict_team_creation_to_admins",
                table: "platform_policy",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "auth_audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_by_subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                    table.ForeignKey(
                        name: "FK_teams_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "team_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_by_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_invites", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_invites_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_invites_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_members_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_invites_created_by_subject_id",
                table: "team_invites",
                column: "created_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_invites_team_id",
                table: "team_invites",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_invites_token_hash",
                table: "team_invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_members_subject_id",
                table: "team_members",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_team_id_subject_id",
                table: "team_members",
                columns: new[] { "team_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teams_created_by_subject_id",
                table: "teams",
                column: "created_by_subject_id");

            const string unpinned = "NULLIF(current_setting('app.current_subject_id', true), '') IS NULL";
            const string pin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";

            migrationBuilder.Sql("ALTER TABLE team_members ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE team_members FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY subject_pin ON team_members
                    USING ({unpinned} OR subject_id = {pin})
                    WITH CHECK ({unpinned} OR subject_id = {pin});
                """);

            migrationBuilder.Sql("ALTER TABLE teams ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE teams FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY subject_pin ON teams
                    USING ({unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = teams.id AND m.subject_id = {pin}))
                    WITH CHECK ({unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = teams.id AND m.subject_id = {pin}));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS subject_pin ON teams;");

            migrationBuilder.DropTable(
                name: "team_invites");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropColumn(
                name: "restrict_team_creation_to_admins",
                table: "platform_policy");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "auth_audit_log");
        }
    }
}
