using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachMarkStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coach_mark_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mark_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coach_mark_states", x => x.id);
                    table.ForeignKey(
                        name: "FK_coach_mark_states_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_coach_mark_states_subject_id_mark_key",
                table: "coach_mark_states",
                columns: new[] { "subject_id", "mark_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coach_mark_states_tenant_id",
                table: "coach_mark_states",
                column: "tenant_id");

            migrationBuilder.Sql("ALTER TABLE coach_mark_states ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE coach_mark_states FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON coach_mark_states
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON coach_mark_states;");
            migrationBuilder.Sql("ALTER TABLE coach_mark_states NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE coach_mark_states DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "coach_mark_states");
        }
    }
}
