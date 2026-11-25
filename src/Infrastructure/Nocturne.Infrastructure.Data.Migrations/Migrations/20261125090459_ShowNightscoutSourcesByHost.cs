using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowNightscoutSourcesByHost : Migration
    {
        /// <summary>
        /// Rewrites the URL of every API-mode migration source, and the description of each of its
        /// runs, to the form <c>NightscoutAddress.Shown</c> gives: host and port, then <c>/…</c> when a
        /// path follows. It is read off the stored <c>NightscoutAddress.Masked</c> form, which has no
        /// scheme, query or user info. A <c>\</c> also starts the path: <c>MaskNightscoutSourceUrls</c>
        /// ended a legacy URL's host only at a <c>/</c>, so a raw URL with a backslash path was stored
        /// whole as its host. The digest identifier is not read or changed, so no two sources merge.
        /// The migration tables carry no row level security, so no tenant context is set.
        /// </summary>
        internal const string RewriteSql = """
            UPDATE migration_sources AS source
            SET nightscout_url = shown.url
            FROM (
                SELECT id, CASE WHEN bare ~ '[/\\]' THEN substring(bare FROM '^[^/\\]*') || '/…' ELSE bare END AS url
                FROM (
                    SELECT id, rtrim(nightscout_url, '/\') AS bare
                    FROM migration_sources
                    WHERE mode = 'Api' AND nightscout_url IS NOT NULL
                ) cut
            ) shown
            WHERE source.id = shown.id AND source.nightscout_url IS DISTINCT FROM shown.url;

            UPDATE migration_runs AS run
            SET source_description = source.nightscout_url
            FROM migration_sources AS source
            WHERE run.source_id = source.id AND source.mode = 'Api' AND source.nightscout_url IS NOT NULL
                AND run.source_description IS NOT NULL
                AND run.source_description IS DISTINCT FROM source.nightscout_url;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RewriteSql);
        }

        /// <inheritdoc />
        /// <remarks>Not undone: a shown URL cannot give back the path it was read from.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
