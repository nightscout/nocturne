using System.Text.RegularExpressions;
using Nocturne.Infrastructure.Data.Migrations;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// An interrupted <c>CONCURRENTLY</c> build leaves an <c>indisvalid = false</c> index that still
/// owns the name, and <c>IF NOT EXISTS</c> matches it and skips — so the retry reports success over
/// an index no query can use and every write still maintains.
/// <see cref="ConcurrentIndexBuilder"/> is the only shape that repairs that, so a migration
/// writing the statement itself has no way back from its own first failure.
/// <para>
/// The same builder is the only way to index a table the migration did not create. The fluent
/// <c>CreateIndex</c> that <c>dotnet ef migrations add</c> emits is a plain <c>CREATE INDEX</c>,
/// which takes a <c>ShareLock</c> and blocks writes for the whole build; a model change that only
/// edits a partial index's filter generates exactly that. A raw <c>CREATE INDEX</c> without
/// <c>CONCURRENTLY</c> does the same, so both shapes fail the rule.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ConcurrentIndexBuildGuardTests
{
    [Fact]
    public void NoMigrationBuildsAConcurrentIndexOutsideConcurrentIndexBuilder()
    {
        var offenders = MigrationSourceFiles.All()
            .Where(f => RawConcurrentBuild.IsMatch(File.ReadAllText(f)))
            .Select(MigrationSourceFiles.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "a concurrent build must go through ConcurrentIndexBuilder.Build, which drops the "
            + "invalid remains of an interrupted earlier run before rebuilding");
    }

    [Fact]
    public void NoNewMigrationBuildsAPlainIndexOnAnExistingTable()
    {
        var offenders = PlainIndexesInAllMigrations().Except(ShippedPlainIndexes).ToList();

        offenders.Should().BeEmpty(
            "a plain index build on a table the migration did not create blocks its writers for the "
            + "whole build; build it with ConcurrentIndexBuilder.Build, replacing the generated "
            + "CreateIndex or the raw CREATE INDEX. "
            + $"A table reported as '{MigrationSourceFiles.UnresolvedTable}' is one the guard could not "
            + "read off the call; unroll the loop so the call names the table literally");
    }

    [Fact]
    public void EveryShippedPlainIndexStillExists()
    {
        // Also the discovery check: a moved directory or a pattern that stopped matching empties
        // the set, and an empty set is not the allowlist.
        ShippedPlainIndexes.Should().BeSubsetOf(PlainIndexesInAllMigrations(),
            "an allowlist entry that no longer matches is stale and hides a regression");
    }

    [Fact]
    public void TheGuardSeesAPlainIndexBuildOnAnExistingTableAndNoOtherShape()
    {
        const string existing = """
            migrationBuilder.CreateIndex(
                name: "ix_notes_tenant_source",
                table: "notes",
                columns: new[] { "tenant_id", "data_source" },
                filter: "deleted_at IS NULL");
            """;
        PlainIndexesOnExistingTables(existing).Should().Equal(new[] { ("notes", "ix_notes_tenant_source") });

        PlainIndexesOnExistingTables("""migrationBuilder.CreateIndex("ix_notes_x", "notes", "x");""")
            .Should().Equal(new[] { ("notes", "ix_notes_x") }, "a positional call names its table second");

        PlainIndexesOnExistingTables("""migrationBuilder.CreateIndex(name: "ix_x", table: table, column: "x");""")
            .Should().Equal(new[] { (MigrationSourceFiles.UnresolvedTable, "ix_x") }, "an unreadable table is reported, not skipped");

        const string created = """
            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new { id = table.Column<Guid>(nullable: false) });

            migrationBuilder.CreateIndex(
                name: "ix_teams_tenant",
                table: "teams",
                column: "tenant_id");
            """;
        PlainIndexesOnExistingTables(created).Should().BeEmpty("a table the migration creates holds no rows to block");

        const string built = """
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_notes_tenant_source",
                "ON notes (tenant_id, data_source) WHERE deleted_at IS NULL");
            """;
        PlainIndexesOnExistingTables(built).Should().BeEmpty();

        PlainIndexesOnExistingTables(
                """migrationBuilder.CreateIndex(name: "ix_x", schema: "public", table: "notes", column: "x");""")
            .Should().Equal(new[] { ("notes", "ix_x") }, "a schema argument does not hide the table");

        PlainIndexesOnExistingTables(
                """migrationBuilder.CreateIndex(name: "ix_u", table: "notes", column: "x", unique: true);""")
            .Should().Equal(new[] { ("notes", "ix_u") }, "a unique plain index blocks writers as well");

        const string createdBySql = """
            migrationBuilder.Sql("CREATE TABLE IF NOT EXISTS teams (id uuid, tenant_id uuid);");
            migrationBuilder.CreateIndex(
                name: "ix_teams_tenant",
                table: "teams",
                column: "tenant_id");
            """;
        PlainIndexesOnExistingTables(createdBySql).Should().BeEmpty("a table created by raw SQL holds no rows to block");
    }

    [Fact]
    public void TheGuardSeesARawPlainIndexOnAnExistingTableAndNoOtherShape()
    {
        PlainIndexesOnExistingTables("""migrationBuilder.Sql("CREATE INDEX ix_x ON notes (c);");""")
            .Should().Equal(new[] { ("notes", "ix_x") });

        PlainIndexesOnExistingTables("""migrationBuilder.Sql("create unique index if not exists ix_y on public.notes (c);");""")
            .Should().Equal(new[] { ("notes", "ix_y") }, "a unique index, a schema qualifier and lower case are read the same way");

        PlainIndexesOnExistingTables("""migrationBuilder.Sql($"CREATE INDEX ix_z ON {table} (c);");""")
            .Should().Equal(new[] { (MigrationSourceFiles.UnresolvedTable, "ix_z") }, "an unreadable table is reported, not skipped");

        PlainIndexesOnExistingTables(
                """migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_x ON notes (c);");""")
            .Should().BeEmpty("the concurrent build is the other guard's concern");

        const string created = """
            migrationBuilder.Sql("CREATE TABLE teams (id uuid, tenant_id uuid);");
            migrationBuilder.Sql("CREATE INDEX ix_teams_tenant ON teams (tenant_id);");
            """;
        PlainIndexesOnExistingTables(created).Should().BeEmpty("a table the migration creates holds no rows to block");
    }

    [Fact]
    public void TheGuardSeesMigrationsAndTheShapeItForbids()
    {
        // Nothing offends any more, so an empty file set or a broken pattern clears the guard above.
        MigrationSourceFiles.All().Should().NotBeEmpty();

        RawConcurrentBuild
            .IsMatch("""Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_x ON t (c);");""")
            .Should().BeTrue();

        RawConcurrentBuild
            .IsMatch("""Sql("CREATE UNIQUE INDEX CONCURRENTLY ix_x ON t (c);");""")
            .Should().BeTrue();
    }

    /// <summary>
    /// The plain index builds on an existing table in migrations that shipped before this guard,
    /// each as <c>migration -> table (index)</c>. They have run on instances, none is on a
    /// record table, and the next release squash folds them into the baseline.
    /// </summary>
    private static readonly IReadOnlySet<string> ShippedPlainIndexes = new HashSet<string>(StringComparer.Ordinal)
    {
        "20261122160931_AddTenantSetupOwnerSubject -> tenants (ix_tenants_setup_owner_subject_id)",
        "20261123023215_AddMembershipNextReasonLapse -> tenant_members (ix_tenant_members_next_reason_lapse_at)",
        "20261123104146_AddTeamMembershipReasonSource -> tenant_member_reasons (IX_tenant_member_reasons_source_id)",
        "20261123213408_AddTeamStaff -> team_members (IX_team_members_created_from_staff_invite_id)",
        "20261126231431_NarrowTeamGroupsForStaff -> team_group_staff (IX_team_group_staff_team_id)",
    };

    private static IReadOnlyList<string> PlainIndexesInAllMigrations() =>
        MigrationSourceFiles.All()
            .SelectMany(f => PlainIndexesOnExistingTables(MigrationSourceFiles.UpBody(f))
                .Select(x => $"{MigrationSourceFiles.Name(f)} -> {x.Table} ({x.Index})"))
            .ToList();

    /// <summary>
    /// The (table, index) of each fluent <c>CreateIndex</c> and each raw <c>CREATE INDEX</c> without
    /// <c>CONCURRENTLY</c> in <paramref name="up"/> on a table the same <c>Up</c> does not create.
    /// Detected on raw text, as <see cref="RawConcurrentBuild"/> is; the created tables are read with
    /// comments blanked, so a commented-out create exempts nothing.
    /// </summary>
    private static IReadOnlyList<(string Table, string Index)> PlainIndexesOnExistingTables(string up)
    {
        var created = MigrationSourceFiles.TablesCreatedIn(MigrationSourceFiles.WithCommentsBlanked(up));

        var fluent = FluentCreateIndex.Matches(up)
            .Select(m => (Table: MigrationSourceFiles.FluentTable(m.Groups[1].Value), Index: FirstLiteral.Match(m.Groups[1].Value).Groups[1].Value));
        var raw = RawPlainIndex.Matches(up)
            .Select(m => (Table: MigrationSourceFiles.ResolveTable(m.Groups[2].Value), Index: m.Groups[1].Value));

        return fluent.Concat(raw).Where(x => !created.Contains(x.Table)).ToList();
    }

    private static readonly Regex RawPlainIndex = new(
        @"CREATE\s+(?:UNIQUE\s+)?INDEX(?!\s+CONCURRENTLY)(?:\s+IF\s+NOT\s+EXISTS)?\s+(\S+)\s+ON\s+(?:ONLY\s+)?(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FluentCreateIndex = new(
        @"\.CreateIndex\s*\(([^;]*?)\)\s*;", RegexOptions.Compiled);

    private static readonly Regex FirstLiteral = new(@"""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Matched against raw text rather than
    /// <see cref="MigrationSourceFiles.WithCommentsBlanked"/>: this path detects offenders, where
    /// withheld evidence reads as a pass.
    /// </summary>
    private static readonly Regex RawConcurrentBuild = new(
        @"CREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
}
