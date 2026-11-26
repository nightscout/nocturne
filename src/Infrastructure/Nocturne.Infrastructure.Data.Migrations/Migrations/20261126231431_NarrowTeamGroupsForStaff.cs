using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NarrowTeamGroupsForStaff : Migration
    {
        private const string Unpinned = "NULLIF(current_setting('app.current_subject_id', true), '') IS NULL";
        private const string Pin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";

        private static string InGroup(string groupId) =>
            $"EXISTS (SELECT 1 FROM team_group_staff s WHERE s.team_group_id = {groupId} AND s.team_member_id = m.id)";

        private static string ManagerOf(string teamId) =>
            $"EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = {teamId} AND m.subject_id = {Pin} AND m.kind = 'manager')";

        /// <summary>
        /// The team each table's update and delete are checked against. <c>subject_pin</c> is FOR ALL, and its USING
        /// now lets staff see their group's rows. PostgreSQL checks a DELETE against USING alone, and an UPDATE's
        /// new row against a WITH CHECK that asks only whether the caller manages the new row's team, so a staff
        /// member who manages any team could move a row of their group into it. <c>manager_update</c> refuses to
        /// touch a row of a team the caller does not manage, and <c>manager_delete</c> refuses to remove one.
        /// <c>team_group_staff</c> names its team itself, since reading <c>team_groups</c> from its policy would
        /// recurse.
        /// </summary>
        private static readonly (string Table, string Team)[] ManagedTeams =
        [
            ("team_groups", "team_groups.team_id"),
            ("team_group_people", "(SELECT g.team_id FROM team_groups g WHERE g.id = team_group_people.team_group_id)"),
            ("team_group_staff", "team_group_staff.team_id"),
        ];

        private static readonly string[] ManagerCommands = ["UPDATE", "DELETE"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "team_group_staff",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("""
                UPDATE team_group_staff s SET team_id = g.team_id FROM team_groups g WHERE g.id = s.team_group_id;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_team_group_staff_team_id",
                table: "team_group_staff",
                column: "team_id");

            migrationBuilder.AddForeignKey(
                name: "FK_team_group_staff_teams_team_id",
                table: "team_group_staff",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql($"""
                ALTER POLICY subject_pin ON team_group_staff
                    USING ({Unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = team_group_staff.team_id AND m.subject_id = {Pin}
                        AND (m.kind = 'manager' OR m.id = team_group_staff.team_member_id)))
                    WITH CHECK ({Unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = team_group_staff.team_id AND m.subject_id = {Pin}
                        AND m.kind = 'manager'));
                """);
            migrationBuilder.Sql($"""
                ALTER POLICY subject_pin ON team_groups
                    USING ({Unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = team_groups.team_id AND m.subject_id = {Pin}
                        AND (m.kind = 'manager' OR {InGroup("team_groups.id")})));
                """);
            migrationBuilder.Sql($"""
                ALTER POLICY subject_pin ON team_group_people
                    USING ({Unpinned} OR EXISTS (SELECT 1 FROM team_groups g JOIN team_members m ON m.team_id = g.team_id
                        WHERE g.id = team_group_people.team_group_id AND m.subject_id = {Pin}
                        AND (m.kind = 'manager' OR {InGroup("g.id")})));
                """);

            foreach (var (table, team) in ManagedTeams)
                foreach (var command in ManagerCommands)
                    migrationBuilder.Sql($"""
                        CREATE POLICY manager_{command.ToLowerInvariant()} ON {table} AS RESTRICTIVE FOR {command}
                            USING ({Unpinned} OR {ManagerOf(team)});
                        """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, _) in ManagedTeams)
                foreach (var command in ManagerCommands)
                    migrationBuilder.Sql($"DROP POLICY manager_{command.ToLowerInvariant()} ON {table};");

            migrationBuilder.Sql($"""
                ALTER POLICY subject_pin ON team_groups
                    USING ({Unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = team_groups.team_id AND m.subject_id = {Pin}));
                """);
            foreach (var table in new[] { "team_group_people", "team_group_staff" })
            {
                var team = $"(SELECT g.team_id FROM team_groups g WHERE g.id = {table}.team_group_id)";
                migrationBuilder.Sql($"""
                    ALTER POLICY subject_pin ON {table}
                        USING ({Unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = {team} AND m.subject_id = {Pin}))
                        WITH CHECK ({Unpinned} OR EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = {team} AND m.subject_id = {Pin} AND m.kind = 'manager'));
                    """);
            }

            migrationBuilder.DropForeignKey(
                name: "FK_team_group_staff_teams_team_id",
                table: "team_group_staff");

            migrationBuilder.DropIndex(
                name: "IX_team_group_staff_team_id",
                table: "team_group_staff");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "team_group_staff");
        }
    }
}
