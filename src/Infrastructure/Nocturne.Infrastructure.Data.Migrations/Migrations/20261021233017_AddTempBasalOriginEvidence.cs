using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds <c>temp_basals.origin_evidence</c>. Existing rows stay null and are classified by the
    /// background corroboration job (TempBasalOriginCorroborator) rather than here.
    /// </summary>
    public partial class AddTempBasalOriginEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin_evidence",
                table: "temp_basals",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "origin_evidence",
                table: "temp_basals");
        }
    }
}
