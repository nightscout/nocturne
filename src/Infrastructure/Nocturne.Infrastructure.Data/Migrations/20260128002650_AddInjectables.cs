using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInjectables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "injectable_dose_id",
                table: "treatments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "injectable_medications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    concentration = table.Column<int>(type: "integer", nullable: false),
                    unit_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    dia = table.Column<double>(type: "double precision", nullable: true),
                    onset = table.Column<double>(type: "double precision", nullable: true),
                    peak = table.Column<double>(type: "double precision", nullable: true),
                    duration = table.Column<double>(type: "double precision", nullable: true),
                    default_dose = table.Column<double>(type: "double precision", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sys_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_injectable_medications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pen_vials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    injectable_medication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<long>(type: "bigint", nullable: true),
                    expires_at = table.Column<long>(type: "bigint", nullable: true),
                    initial_units = table.Column<double>(type: "double precision", nullable: true),
                    remaining_units = table.Column<double>(type: "double precision", nullable: true),
                    lot_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Active"),
                    notes = table.Column<string>(type: "text", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sys_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pen_vials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pen_vials_injectable_medications_injectable_medication_id",
                        column: x => x.injectable_medication_id,
                        principalTable: "injectable_medications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "injectable_doses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    injectable_medication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    units = table.Column<double>(type: "double precision", nullable: false),
                    timestamp = table.Column<long>(type: "bigint", nullable: false),
                    injection_site = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    pen_vial_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lot_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    entered_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    original_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_injectable_doses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_injectable_doses_injectable_medications_injectable_medicati~",
                        column: x => x.injectable_medication_id,
                        principalTable: "injectable_medications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_injectable_doses_pen_vials_pen_vial_id",
                        column: x => x.pen_vial_id,
                        principalTable: "pen_vials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_treatments_injectable_dose_id",
                table: "treatments",
                column: "injectable_dose_id",
                filter: "injectable_dose_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_doses_injectable_medication_id",
                table: "injectable_doses",
                column: "injectable_medication_id");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_doses_medication_timestamp",
                table: "injectable_doses",
                columns: new[] { "injectable_medication_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_injectable_doses_pen_vial_id",
                table: "injectable_doses",
                column: "pen_vial_id",
                filter: "pen_vial_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_doses_sys_created_at",
                table: "injectable_doses",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_doses_timestamp",
                table: "injectable_doses",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_injectable_medications_category",
                table: "injectable_medications",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_medications_is_archived",
                table: "injectable_medications",
                column: "is_archived");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_medications_name",
                table: "injectable_medications",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_medications_sort_order",
                table: "injectable_medications",
                column: "sort_order");

            migrationBuilder.CreateIndex(
                name: "ix_injectable_medications_sys_created_at",
                table: "injectable_medications",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_expires_at",
                table: "pen_vials",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_injectable_medication_id",
                table: "pen_vials",
                column: "injectable_medication_id");

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_is_archived",
                table: "pen_vials",
                column: "is_archived");

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_medication_status",
                table: "pen_vials",
                columns: new[] { "injectable_medication_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_opened_at",
                table: "pen_vials",
                column: "opened_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_status",
                table: "pen_vials",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_pen_vials_sys_created_at",
                table: "pen_vials",
                column: "sys_created_at");

            migrationBuilder.AddForeignKey(
                name: "FK_treatments_injectable_doses_injectable_dose_id",
                table: "treatments",
                column: "injectable_dose_id",
                principalTable: "injectable_doses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_treatments_injectable_doses_injectable_dose_id",
                table: "treatments");

            migrationBuilder.DropTable(
                name: "injectable_doses");

            migrationBuilder.DropTable(
                name: "pen_vials");

            migrationBuilder.DropTable(
                name: "injectable_medications");

            migrationBuilder.DropIndex(
                name: "ix_treatments_injectable_dose_id",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "injectable_dose_id",
                table: "treatments");
        }
    }
}
