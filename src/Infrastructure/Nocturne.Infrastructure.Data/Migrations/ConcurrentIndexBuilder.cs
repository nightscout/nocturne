using Microsoft.EntityFrameworkCore.Migrations;

namespace Nocturne.Infrastructure.Data.Migrations;

/// <summary>
/// Builds an index with <c>CONCURRENTLY</c>, which every index over one of the large record tables
/// needs. A plain <c>CREATE INDEX</c> takes a <c>ShareLock</c>, which blocks every write to that
/// table until the build finishes — on the tables the connectors ingest into, that is dropped
/// uploads. Batching them into one transaction is worse on both counts: it holds every one of
/// those locks until the commit, and a failure part-way discards all the completed work, so under
/// <c>restart: unless-stopped</c> each crash-loop iteration starts again from nothing. That is the
/// same trap the hour-long command timeout in
/// <see cref="Extensions.DatabaseInitializationExtensions.RunMigrationsAsync"/> exists to avoid.
/// A self-hosted deployment may also run more than one API replica, where a lock held for the
/// length of a multi-GB build is not confined to one process's startup.
/// <para>
/// <c>CONCURRENTLY</c> cannot run inside a transaction, so the migration is not atomic and an
/// interrupted build leaves an <c>indisvalid = false</c> index behind. <c>IF NOT EXISTS</c> does
/// not repair that — it matches the relation name and skips, so the retry reports success over an
/// index the planner will never use. Every build therefore drops its own invalid remains first,
/// conditional on invalidity so a retry does not rebuild what already succeeded.
/// </para>
/// <para>
/// One consequence of leaving the transaction, for the multi-replica case above: EF's migration
/// lock is <c>LOCK TABLE … IN ACCESS EXCLUSIVE MODE</c> on the history table, not an advisory
/// lock, so it is transaction-scoped and released at each <c>suppressTransaction</c> boundary
/// rather than held for the migration. Two replicas starting together can therefore interleave,
/// and one's invalid-index drop can land on the other's in-flight build. It self-heals — the
/// interrupted build leaves remains the next iteration clears — but a deployment scaling the API
/// should migrate once before scaling out rather than rely on that.
/// </para>
/// </summary>
internal static class ConcurrentIndexBuilder
{
    /// <param name="migrationBuilder">The migration being written.</param>
    /// <param name="name">Unqualified index name. Resolved against <c>public</c> for the validity
    /// probe, matching the schema these migrations create in.</param>
    /// <param name="definition">Everything after the index name: <c>ON table (cols) WHERE …</c>.</param>
    public static void Build(MigrationBuilder migrationBuilder, string name, string definition)
    {
        migrationBuilder.Sql($"""
            DO $$
            BEGIN
                SET LOCAL lock_timeout = '3s';
                IF EXISTS (
                    SELECT 1 FROM pg_index
                    WHERE indexrelid = to_regclass('public.{name}') AND NOT indisvalid)
                THEN
                    EXECUTE 'DROP INDEX public.{name}';
                    RAISE NOTICE 'dropped invalid {name} left by an interrupted build';
                END IF;
            END $$;
            """);

        migrationBuilder.Sql(
            $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {name} {definition};",
            suppressTransaction: true);
    }

    /// <summary>Drops an index without blocking writers, for the reverse direction.</summary>
    public static void Drop(MigrationBuilder migrationBuilder, string name) =>
        migrationBuilder.Sql(
            $"DROP INDEX CONCURRENTLY IF EXISTS {name};",
            suppressTransaction: true);
}
