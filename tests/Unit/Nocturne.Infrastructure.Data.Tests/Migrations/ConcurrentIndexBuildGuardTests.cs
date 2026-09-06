using System.Text.RegularExpressions;
using Nocturne.Infrastructure.Data.Migrations;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// An interrupted <c>CONCURRENTLY</c> build leaves an <c>indisvalid = false</c> index that still
/// owns the name, and <c>IF NOT EXISTS</c> matches it and skips — so the retry reports success over
/// an index no query can use and every write still maintains.
/// <see cref="ConcurrentIndexBuilder"/> is the only shape that repairs that, so a migration
/// writing the statement itself has no way back from its own first failure.
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
    /// Matched against raw text rather than
    /// <see cref="MigrationSourceFiles.WithCommentsBlanked"/>: this path detects offenders, where
    /// withheld evidence reads as a pass.
    /// </summary>
    private static readonly Regex RawConcurrentBuild = new(
        @"CREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
}
