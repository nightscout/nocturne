using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamFlagRules : Migration
    {
        private const string Pin = "NULLIF(current_setting('app.current_subject_id', true), '')::uuid";

        private static string MemberOf(string teamId, bool manager) =>
            $"EXISTS (SELECT 1 FROM team_members m WHERE m.team_id = {teamId} AND m.subject_id = {Pin}"
            + (manager ? " AND m.kind = 'manager')" : ")");

        /// <summary>
        /// <c>team_pin</c> is FOR ALL, and its USING lets every member of the team see its rules. PostgreSQL checks a
        /// DELETE against USING alone, so a staff member could delete one; <c>manager_update</c> and
        /// <c>manager_delete</c> refuse to touch a team's rule for anyone but its managers, and pass a personal rule
        /// for its owner. Neither policy admits a context with no subject pinned.
        /// </summary>
        private static readonly string[] ManagerCommands = ["UPDATE", "DELETE"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE POLICY team_pin ON flag_rules
                    USING (team_id IS NOT NULL AND {MemberOf("flag_rules.team_id", manager: false)})
                    WITH CHECK (team_id IS NOT NULL AND {MemberOf("flag_rules.team_id", manager: true)});
                """);

            foreach (var command in ManagerCommands)
                migrationBuilder.Sql($"""
                    CREATE POLICY manager_{command.ToLowerInvariant()} ON flag_rules AS RESTRICTIVE FOR {command}
                        USING (subject_id = {Pin} OR {MemberOf("flag_rules.team_id", manager: true)});
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var command in ManagerCommands)
                migrationBuilder.Sql($"DROP POLICY manager_{command.ToLowerInvariant()} ON flag_rules;");

            migrationBuilder.Sql("DROP POLICY team_pin ON flag_rules;");
        }
    }
}
