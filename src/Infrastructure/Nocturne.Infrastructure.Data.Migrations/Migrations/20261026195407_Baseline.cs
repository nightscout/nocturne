using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Every migration up to and including 20261026195407_AddReleaseNotices, the last of v0.3.0,
    /// as the script pg_dump takes of a database migrated that far. Written by
    /// <c>scripts/squash-migrations.cs</c>; regenerate it rather than editing it.
    /// </summary>
    [MigrationBaseline("v0.3.0")]
    public partial class Baseline : Migration
    {
        private const string Script = "Nocturne.Infrastructure.Data.Migrations.Baseline.sql";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(Baseline).Assembly.GetManifestResourceStream(Script)
                ?? throw new InvalidOperationException($"The embedded resource {Script} is missing.");
            using var reader = new StreamReader(stream);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("The baseline is the first migration; there is nothing before it to revert to.");
    }
}
