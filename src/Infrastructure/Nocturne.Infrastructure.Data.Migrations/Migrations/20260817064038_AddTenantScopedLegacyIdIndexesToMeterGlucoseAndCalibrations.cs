using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantScopedLegacyIdIndexesToMeterGlucoseAndCalibrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_meter_glucose_legacy_id",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "ix_calibrations_legacy_id",
                table: "calibrations");

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_tenant_legacy_id",
                table: "meter_glucose",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_tenant_legacy_id",
                table: "calibrations",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_meter_glucose_tenant_legacy_id",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "ix_calibrations_tenant_legacy_id",
                table: "calibrations");

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_legacy_id",
                table: "meter_glucose",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_legacy_id",
                table: "calibrations",
                column: "legacy_id");
        }
    }
}
