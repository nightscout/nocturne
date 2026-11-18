using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// <c>written_live</c> on <c>state_spans</c>, as <c>AddWriteBackIdentityTracking</c> added it to the
    /// other legacy-keyed tables: an override or temporary target is written back as a treatment, so a
    /// pulled copy of a span a live write touched is its echo. Existing rows start false.
    /// </summary>
    public partial class AddWrittenLiveToStateSpans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "state_spans",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "written_live",
                table: "state_spans");
        }
    }
}
