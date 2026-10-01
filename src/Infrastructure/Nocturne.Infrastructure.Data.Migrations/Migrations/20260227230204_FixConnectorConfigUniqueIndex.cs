using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixConnectorConfigUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_connector_configurations_connector_name",
                table: "connector_configurations");

            migrationBuilder.CreateIndex(
                name: "ix_connector_configurations_connector_name_tenant",
                table: "connector_configurations",
                columns: new[] { "connector_name", "tenant_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_connector_configurations_connector_name_tenant",
                table: "connector_configurations");

            migrationBuilder.CreateIndex(
                name: "ix_connector_configurations_connector_name",
                table: "connector_configurations",
                column: "connector_name",
                unique: true);
        }
    }
}
