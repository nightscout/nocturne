using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTempBasalPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "rate",
                table: "temp_basals",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<double>(
                name: "percent",
                table: "temp_basals",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "rate_from_schedule",
                table: "temp_basals",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No backfill: a null rate is unknown, and 0 would read as a suspension. The NOT NULL
            // check sees every row, which a query under FORCE ROW LEVEL SECURITY would not.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    ALTER TABLE temp_basals ALTER COLUMN rate SET NOT NULL;
                EXCEPTION WHEN not_null_violation THEN
                    RAISE EXCEPTION 'temp_basals holds percent temp basals of unknown rate, which a rollback cannot keep without inventing a rate. Re-upload them under a basal schedule, or delete them, first.';
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "percent",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "rate_from_schedule",
                table: "temp_basals");
        }
    }
}
