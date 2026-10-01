using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoopData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "otp",
                table: "treatments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasonDisplay",
                table: "treatments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "remoteAbsorption",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "remoteBolus",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "remoteCarbs",
                table: "treatments",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "otp",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "reasonDisplay",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "remoteAbsorption",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "remoteBolus",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "remoteCarbs",
                table: "treatments");
        }
    }
}
