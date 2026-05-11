using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantTimestampIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_timestamp",
                table: "boluses",
                columns: new[] { "tenant_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_timestamp",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_tenant_start_timestamp",
                table: "temp_basals",
                columns: new[] { "tenant_id", "start_timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_boluses_tenant_timestamp",           table: "boluses");
            migrationBuilder.DropIndex(name: "ix_carb_intakes_tenant_timestamp",      table: "carb_intakes");
            migrationBuilder.DropIndex(name: "ix_temp_basals_tenant_start_timestamp", table: "temp_basals");
        }
    }
}
