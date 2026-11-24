using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaskNightscoutSourceUrls : Migration
    {
        /// <summary>
        /// Rewrites every API-mode migration source to the form the import now writes: its identifier
        /// a SHA-256 digest of the Nightscout base and its URL masked, as
        /// <c>MigrationJob.ApiSourceIdentifier</c> and <c>NightscoutAddress.Masked</c> compute them,
        /// and every run of such a source to the source's masked URL.
        /// </summary>
        /// <remarks>
        /// A source may still hold the raw URL it was first stored under, query and all, so the base is
        /// read here as <c>NightscoutAddress.Display</c> reads it: cut at its query or fragment, no user
        /// info, a scheme-less value taken for https, scheme and host lower-cased, a default port
        /// dropped, no trailing slash. Sources of one tenant can then share a base, which the unique
        /// <c>(tenant_id, source_identifier)</c> index forbids once both hold its digest: the one
        /// already stored under the base wins, then the most recently migrated, then the newest. A
        /// loser's runs move to the winner and its watermarks fold into the winner's before it is
        /// deleted, since deleting it first would cascade to its runs. A source already holding a digest
        /// is left as it is. The migration tables carry no row level security, so no tenant context is
        /// set.
        /// <para>
        /// The mask is wider than <c>NightscoutAddress.Masked</c>: it also cuts every segment holding a
        /// character outside <c>[A-Za-z0-9._~-]</c>, whatever its length. <c>Masked</c> counts the
        /// characters of the path as <c>System.Uri</c> escapes it, and a raw legacy path is not escaped,
        /// so a secret segment that escapes to 16 or more characters can be shorter here. Cutting a
        /// stored display string too far costs nothing; leaving a secret whole does.
        /// </para>
        /// </remarks>
        internal const string RewriteSql = """
            DO $$
            DECLARE
                r RECORD;
            BEGIN
                FOR r IN
                    SELECT id, winner, base,
                           host || regexp_replace(
                               regexp_replace(path, '/([^/]{4})[^/]{12,}', '/\1…', 'g'),
                               '/(?=[^/]*[^A-Za-z0-9._~/-])([^/]{1,4})[^/]*', '/\1…', 'g') AS masked,
                           last_migration_at, last_migrated_data_timestamp, created_at, nightscout_api_secret_hash
                    FROM (
                        SELECT *, first_value(id) OVER (
                            PARTITION BY tenant_id, base
                            ORDER BY source_identifier = base DESC, last_migration_at DESC NULLS LAST, created_at DESC, id
                        ) AS winner
                        FROM (
                            SELECT *, scheme || '://' || host || path AS base
                            FROM (
                                SELECT s.*,
                                       lower(m[1]) AS scheme,
                                       CASE
                                           WHEN lower(m[1]) = 'https' THEN regexp_replace(lower(regexp_replace(m[2], '^.*@', '')), ':443$', '')
                                           WHEN lower(m[1]) = 'http' THEN regexp_replace(lower(regexp_replace(m[2], '^.*@', '')), ':80$', '')
                                           ELSE lower(regexp_replace(m[2], '^.*@', ''))
                                       END AS host,
                                       rtrim(m[3], '/') AS path
                                FROM migration_sources s,
                                     LATERAL (SELECT regexp_replace(btrim(s.source_identifier), '[?#].*$', '') AS cut) c,
                                     LATERAL (SELECT regexp_match(
                                         CASE WHEN c.cut ~ '^[A-Za-z][A-Za-z0-9+.-]*://' THEN c.cut ELSE 'https://' || c.cut END,
                                         '^([A-Za-z][A-Za-z0-9+.-]*)://([^/]*)(.*)$') AS m) p
                                WHERE s.mode = 'Api' AND s.source_identifier !~ '^[0-9a-f]{64}$'
                            ) parsed
                        ) based
                    ) ranked
                    ORDER BY id = winner DESC
                LOOP
                    IF r.id = r.winner THEN
                        UPDATE migration_sources
                        SET source_identifier = encode(sha256(convert_to(r.base, 'UTF8')), 'hex'),
                            nightscout_url = r.masked
                        WHERE id = r.id;
                    ELSE
                        UPDATE migration_runs SET source_id = r.winner WHERE source_id = r.id;
                        UPDATE migration_sources
                        SET last_migration_at = GREATEST(last_migration_at, r.last_migration_at),
                            last_migrated_data_timestamp = GREATEST(last_migrated_data_timestamp, r.last_migrated_data_timestamp),
                            created_at = LEAST(created_at, r.created_at),
                            nightscout_api_secret_hash = COALESCE(nightscout_api_secret_hash, r.nightscout_api_secret_hash)
                        WHERE id = r.winner;
                        DELETE FROM migration_sources WHERE id = r.id;
                    END IF;
                END LOOP;

                UPDATE migration_runs AS run
                SET source_description = source.nightscout_url
                FROM migration_sources AS source
                WHERE run.source_id = source.id AND source.mode = 'Api' AND run.source_description IS NOT NULL;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RewriteSql);
        }

        /// <inheritdoc />
        /// <remarks>Not undone: a digest and a masked URL cannot give back the URL they were read from.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
