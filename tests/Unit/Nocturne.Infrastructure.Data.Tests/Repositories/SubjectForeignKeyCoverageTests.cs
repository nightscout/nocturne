using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The rule that makes it safe to delete a subject the unclaimed predicates find: every foreign key
/// to <c>subjects</c> is read by <c>SubjectRepository.Unclaimed</c> or <c>SubjectRepository.TenantRowsNaming</c>,
/// or is listed below with the reason a subject it names is refused or the delete may take the row.
/// The tables the predicates read come from the SQL of the predicates themselves.
/// </summary>
[Trait("Category", "Unit")]
public class SubjectForeignKeyCoverageTests
{
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        ["tenants.setup_owner_subject_id"] =
            "set to null by the delete: a subject it names is one the owner step made, and deleting it ends the record",
    };

    [Fact]
    public void EveryForeignKeyToSubjectsIsNamedByAPredicateOrListed()
    {
        using var db = OfflineDbContext.Create();
        var read = PredicateSql(db);

        var uncovered = ForeignKeysToSubjects(db.Model)
            .Where(key => !Exceptions.ContainsKey(key.Name) && !Reads(read, key.Table, key.Column))
            .Select(key => $"{key.Name} ({key.OnDelete})")
            .ToList();

        uncovered.Should().BeEmpty(
            "a foreign key neither predicate reads lets a delete cascade or null a row nothing refused");
    }

    [Fact]
    public void EveryListedExceptionIsStillAForeignKeyNoPredicateReads()
    {
        using var db = OfflineDbContext.Create();
        var read = PredicateSql(db);
        var keys = ForeignKeysToSubjects(db.Model).ToList();

        Exceptions.Keys.Where(name => !keys.Any(key => key.Name == name)).Should().BeEmpty(
            "an entry naming no foreign key would silently cover a future one");
        Exceptions.Keys.Where(name => keys.Any(key => key.Name == name && Reads(read, key.Table, key.Column)))
            .Should().BeEmpty("an entry for a key a predicate now reads has nothing left to say");
    }

    [Fact]
    public void TheTeamForeignKeysAreAmongTheOnesRead()
    {
        using var db = OfflineDbContext.Create();
        var read = PredicateSql(db);

        string[] team =
        [
            "teams.created_by_subject_id", "team_members.subject_id", "team_invites.created_by_subject_id",
            "team_staff_invites.created_by_subject_id", "team_access_links.created_by_subject_id",
            "team_tenant_access.approved_by_subject_id", "team_site_declines.subject_id",
        ];
        var keys = ForeignKeysToSubjects(db.Model).ToList();

        keys.Select(key => key.Name).Should().Contain(team);
        keys.Where(key => team.Contains(key.Name)).Where(key => !Reads(read, key.Table, key.Column))
            .Should().BeEmpty();
    }

    [Fact]
    public void ReadsOnlyWithinOneStatementWhenAnAliasIsReused()
    {
        string[] statements =
        [
            "SELECT 1 FROM a_table AS o WHERE o.x = 1",
            "SELECT 1 FROM b_table AS o WHERE o.subject_id = 1",
        ];

        Reads(statements, "a_table", "subject_id").Should().BeFalse();
        Reads(statements, "b_table", "subject_id").Should().BeTrue();
        Reads(string.Join("\n;\n", statements), "a_table", "subject_id").Should().BeTrue(
            "joined, the alias of the second statement answers for the first, which is the hole matching per statement closes");
    }

    [Theory]
    [InlineData("SELECT 1 FROM team_members AS t WHERE t.subject_id = s.id", "team_members", "subject_id", true)]
    [InlineData("SELECT t.id FROM team_members AS t WHERE t.tenant_id = @a AND t.subject_id = @p", "team_members", "subject_id", true)]
    [InlineData("SELECT 1 FROM \"team_members\" AS \"t\" WHERE \"t\".\"team_id\" = \"s\".\"id\"", "team_members", "subject_id", false)]
    [InlineData("SELECT 1 FROM \"team_invites\" AS \"t\" WHERE \"t\".\"subject_id\" = \"s\".\"id\"", "team_members", "subject_id", false)]
    [InlineData("SELECT 1 FROM \"team_members\" AS \"t\" WHERE \"s\".\"subject_id\" = 1", "team_members", "subject_id", false)]
    public void ReadsOnlyAColumnOfTheTableNamed(string sql, string table, string column, bool expected) =>
        Reads(sql, table, column).Should().Be(expected);

    private sealed record SubjectKey(string Table, string Column, DeleteBehavior OnDelete)
    {
        public string Name => $"{Table}.{Column}";
    }

    private static IEnumerable<SubjectKey> ForeignKeysToSubjects(IModel model) => model.GetEntityTypes()
        .SelectMany(type => type.GetForeignKeys())
        .Where(key => key.PrincipalEntityType.ClrType == typeof(SubjectEntity))
        .Select(key =>
        {
            var table = key.DeclaringEntityType.GetTableName()!;
            var column = key.Properties
                .Select(p => p.GetColumnName(StoreObjectIdentifier.Table(table, key.DeclaringEntityType.GetSchema())))
                .Single(c => c != "tenant_id");
            return new SubjectKey(table, column!, key.DeleteBehavior);
        })
        .DistinctBy(key => key.Name);

    /// <summary>
    /// The SQL of every query the two predicates run, one string per statement, over every shape of caller:
    /// with and without a kept identity, and with and without a setup tenant. EF aliases are unique inside
    /// one statement and repeat between statements, so a read is matched within one statement.
    /// </summary>
    private static List<string> PredicateSql(NocturneDbContext db)
    {
        var tenant = Guid.CreateVersion7();
        var subject = Guid.CreateVersion7();
        var queries = new List<string>
        {
            SubjectRepository.Unclaimed(db, identityId: null, ownerOf: null).ToQueryString(),
            SubjectRepository.Unclaimed(db, identityId: Guid.CreateVersion7(), ownerOf: tenant).ToQueryString(),
        };
        queries.AddRange(SubjectRepository.TenantRowsNaming(db, tenant, subject).Select(rows => rows.ToQueryString()));
        return queries;
    }

    private static bool Reads(IEnumerable<string> statements, string table, string column) =>
        statements.Any(statement => Reads(statement, table, column));

    private static bool Reads(string sql, string table, string column) => Regex.IsMatch(
        sql, $@"FROM ""?{table}""? AS ""?(?<alias>\w+)""?(?s:.*?)(?<!\w)""?\k<alias>""?\.""?{column}(?!\w)");
}
