using Microsoft.EntityFrameworkCore.Migrations;

namespace Nocturne.Infrastructure.Data.Migrations;

/// <summary>
/// Builds an index with <c>CONCURRENTLY</c>, which every index over one of the large record tables
/// needs. The API migrates before it serves, so its own writes never meet the build; the writers
/// that can are a pod still running through its termination grace period while its replacement
/// migrates, and an API serving a database someone runs <c>dotnet ef database update</c> against.
/// A plain <c>CREATE INDEX</c> takes a <c>ShareLock</c>, which blocks those writes until the build
/// finishes — on the tables the connectors ingest into, that is dropped uploads. Batching the
/// builds into one transaction is worse on both counts: it holds every one of those locks until the
/// commit, and a failure part-way discards all the completed work, so under
/// <c>restart: unless-stopped</c> each crash-loop iteration starts again from nothing. That is the
/// same trap the hour-long command timeout in
/// <see cref="Extensions.DatabaseInitializationExtensions.RunMigrationsAsync"/> exists to avoid.
/// <para>
/// <c>CONCURRENTLY</c> cannot run inside a transaction, so the migration is not atomic and an
/// interrupted build leaves an <c>indisvalid = false</c> index behind. <c>IF NOT EXISTS</c> does
/// not repair that — it matches the relation name and skips, so the retry reports success over an
/// index the planner will never use. Every build therefore drops its own invalid remains first,
/// conditional on invalidity so a retry does not rebuild what already succeeded.
/// </para>
/// <para>
/// The <c>lock_timeout</c> covers only that drop. The <c>CREATE INDEX CONCURRENTLY</c> waits for
/// conflicting transactions without a timeout, on purpose: a timeout there would fail the migration
/// and crash-loop the API, where waiting only delays startup.
/// </para>
/// </summary>
internal static class ConcurrentIndexBuilder
{
    /// <param name="migrationBuilder">The migration being written.</param>
    /// <param name="name">Unqualified index name. Resolved against <c>public</c> for the validity
    /// probe, matching the schema these migrations create in.</param>
    /// <param name="definition">Everything after the index name: <c>ON table (cols) WHERE …</c>.</param>
    public static void Build(MigrationBuilder migrationBuilder, string name, string definition) =>
        Create(migrationBuilder, "INDEX", name, definition);

    /// <summary>
    /// <see cref="Build"/> for a unique index. A unique index over a table the migration did not
    /// create needs its loser cleanup earlier in the same <c>Up</c>;
    /// <c>UniqueIndexDeduplicationGuardTests</c> reads the table off <paramref name="definition"/>.
    /// </summary>
    /// <inheritdoc cref="Build" path="/param"/>
    public static void BuildUnique(MigrationBuilder migrationBuilder, string name, string definition) =>
        Create(migrationBuilder, "UNIQUE INDEX", name, definition);

    private static void Create(MigrationBuilder migrationBuilder, string kind, string name, string definition)
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
            $"CREATE {kind} CONCURRENTLY IF NOT EXISTS {name} {definition};",
            suppressTransaction: true);
    }

    /// <summary>Drops an index without blocking writers, for the reverse direction.</summary>
    public static void Drop(MigrationBuilder migrationBuilder, string name) =>
        migrationBuilder.Sql(
            $"DROP INDEX CONCURRENTLY IF EXISTS {name};",
            suppressTransaction: true);
}
