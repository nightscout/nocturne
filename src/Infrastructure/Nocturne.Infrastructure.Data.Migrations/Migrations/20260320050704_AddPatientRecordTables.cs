using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientRecordTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patient_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    aid_algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "patient_insulins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    insulin_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_insulins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "patient_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    diabetes_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    diabetes_type_other = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    diagnosis_date = table.Column<DateOnly>(type: "date", nullable: true),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    preferred_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    pronouns = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    avatar_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_patient_devices_tenant_is_current",
                table: "patient_devices",
                columns: new[] { "tenant_id", "is_current" });

            migrationBuilder.CreateIndex(
                name: "ix_patient_insulins_tenant_is_current",
                table: "patient_insulins",
                columns: new[] { "tenant_id", "is_current" });

            migrationBuilder.CreateIndex(
                name: "ix_patient_records_tenant_id",
                table: "patient_records",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patient_devices");

            migrationBuilder.DropTable(
                name: "patient_insulins");

            migrationBuilder.DropTable(
                name: "patient_records");
        }
    }
}
