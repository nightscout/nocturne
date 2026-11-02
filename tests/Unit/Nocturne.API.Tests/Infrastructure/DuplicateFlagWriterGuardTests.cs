using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>is_duplicate</c> projects <c>linked_records</c> onto each linked record, and
/// <c>DuplicateGroupPrimaries.SetPrimariesAsync</c> keeps the two in step. Any other write to a link,
/// whether a primary moved, a link added or a link deleted, can leave a record's flag disagreeing with
/// its link, so each member that writes either is listed here, by the kind of write it makes, with
/// the reason its write cannot.
/// </summary>
/// <remarks>
/// Syntax only, so a write is recognised by its shape: an assignment or initializer of the flag or of
/// a link's primary, an <c>ExecuteUpdate</c> setter selecting either, a <c>new LinkedRecordEntity</c>,
/// an EF add, remove or bulk delete in a source that names <c>LinkedRecord</c>, and SQL inserting into,
/// updating or deleting from <c>linked_records</c> or setting the flag. <c>patient_insulins</c> has an
/// <c>IsPrimary</c> of its own, so a member assignment of <c>IsPrimary</c> counts only in a source that
/// names <c>LinkedRecord</c>. Migrations are exempt: the backfill sets the flags once, as a migration.
/// <para>
/// Reads are held to the module too: a read decides what it shows by the flag
/// (<c>ReadVisibilityFilter</c>), so a query of the links anywhere else is either a second visibility
/// rule or group bookkeeping the module owns. A read is a member access of <c>LinkedRecords</c>, a
/// <c>Set&lt;LinkedRecordEntity&gt;()</c>, or SQL selecting from or joining <c>linked_records</c>.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public partial class DuplicateFlagWriterGuardTests
{
    private const string Data = "src/Infrastructure/Nocturne.Infrastructure.Data";
    private const string Primary = "primary";
    private const string Flag = "flag";
    private const string Add = "add";
    private const string Delete = "delete";

    private static readonly Dictionary<string, string> Writers = new()
    {
        [$"{Data}/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.SetPrimariesAsync ({Primary})"] =
            "moves primaries, then derives the flags from the links",
        [$"{Data}/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.Derive ({Flag})"] =
            "the flag statement: each flag from its record's link",
        [$"{Data}/Services/DeduplicationService.cs: DeduplicationService.LinkChunkAsync ({Add})"] =
            "inserts links, then flags the new non-primary ones in the same transaction",
        [$"{Data}/Services/DeduplicationService.cs: DeduplicationService.Phase ({Add})"] =
            "builds a pruned singleton's link as the primary of a group of its own: its record stays unflagged",
        [$"{Data}/Services/DeduplicationService.cs: DeduplicationService.MergePrimariesAsync ({Add})"] =
            "stores those primary links, then moves the merged primaries through SetPrimariesAsync",
        [$"{Data}/Services/DeduplicationService.cs: DeduplicationService.PruneNextPageAsync ({Delete})"] =
            "deletes only primary links alone in their group: the record is unflagged before and after",
        [$"{Data}/Services/DeduplicationService.cs: DeduplicationService.DeleteOrphanedLinksAsync ({Delete})"] =
            "deletes only links whose record is gone, so no flag is left behind",
        [$"{Data}/Extensions/PurgeExtensions.cs: PurgeExtensions.PurgeAsync ({Delete})"] =
            "deletes a purged row's own link with the row",
        [$"{Data}/Extensions/RetentionPurgeExtensions.cs: RetentionPurgeExtensions.PurgeOlderThanAsync ({Delete})"] =
            "deletes an aged-out row's own link in the row's statement",
    };

    /// <summary>The deduplication module, whose members may query the links freely.</summary>
    private static readonly string[] Module =
    [
        $"{Data}/Services/DeduplicationService.cs",
        $"{Data}/Extensions/DuplicateGroupPrimaries.cs",
    ];

    private static readonly Dictionary<string, string> Readers = new()
    {
        [$"{Data}/Extensions/PurgeExtensions.cs: PurgeExtensions.PurgeAsync"] =
            "selects the purged rows' own links to delete them",
        [$"{Data}/Extensions/SoftDeleteRestoreExtensions.cs: SoftDeleteRestoreExtensions.CoDeletedCopiesAsync"] =
            "finds the group mates a restore brings back with a row, which is group bookkeeping, not visibility",
    };

    private static readonly IReadOnlyList<(string Path, SyntaxTree Tree, string Text)> Sources = RepoSources.Src
        .Where(s => !s.Path.StartsWith("src/Infrastructure/Nocturne.Infrastructure.Data.Migrations/", StringComparison.Ordinal))
        .Select(s => (s.Path, s.Tree, s.Text))
        .ToList();

    private static readonly IReadOnlyList<string> Found = Sources
        .SelectMany(s => Writes(s.Tree, s.Text).Select(w => $"{s.Path}: {EnclosingMember.Of(w.Node)} ({w.Kind})"))
        .Distinct()
        .ToList();

    private static readonly IReadOnlyList<string> FoundReads = Sources
        .Where(s => !Module.Contains(s.Path))
        .SelectMany(s => Reads(s.Tree).Select(node => $"{s.Path}: {EnclosingMember.Of(node)}"))
        .Distinct()
        .ToList();

    [Fact]
    public void OnlyTheListedMembersWriteLinksOrDuplicateFlags()
    {
        Found.Except(Writers.Keys).Should().BeEmpty(
            "a link written outside DuplicateGroupPrimaries.SetPrimariesAsync can leave is_duplicate disagreeing with it");
    }

    [Fact]
    public void EveryListedMemberStillWrites()
    {
        Writers.Keys.Except(Found).Should().BeEmpty("a stale entry would cover a future writer in that member");
    }

    [Fact]
    public void OnlyTheModuleAndTheListedMembersQueryLinks()
    {
        FoundReads.Except(Readers.Keys).Should().BeEmpty(
            "a read hides duplicates by is_duplicate through ReadVisibilityFilter, never by querying linked_records");
    }

    [Fact]
    public void EveryListedReaderStillQueriesLinks()
    {
        Readers.Keys.Except(FoundReads).Should().BeEmpty("a stale entry would cover a future reader in that member");
        Module.Should().OnlyContain(path => Sources.Any(s => s.Path == path), "a moved module file would exempt nothing");
    }

    [Theory]
    [InlineData("class C { object M(Db db) => db.Notes.Where(n => !db.LinkedRecords.Any(l => l.RecordId == n.Id)); }", true)]
    [InlineData("class C { object M(Db db) => db.Set<LinkedRecordEntity>().Count(); }", true)]
    [InlineData("class C { string Sql => \"SELECT n.* FROM notes n JOIN linked_records l ON l.record_id = n.id\"; }", true)]
    [InlineData("class C { string Sql => \"SELECT 1 FROM public.linked_records WHERE NOT is_primary\"; }", true)]
    [InlineData("class C { string Sql => \"DELETE FROM linked_records WHERE record_id = @id\"; }", false)]
    [InlineData("class C { object M(List<LinkedRecord> all) => new Response { LinkedRecords = all }; }", false)]
    [InlineData("class C { object M(Db db) => db.Notes.Where(n => !n.IsDuplicate); }", false)]
    public void RecognisesARead(string source, bool read) =>
        Reads(CSharpSyntaxTree.ParseText(source)).Any().Should().Be(read);

    private static IEnumerable<SyntaxNode> Reads(SyntaxTree tree)
    {
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (node is MemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.ValueText: "LinkedRecords" } }
                || (node is GenericNameSyntax generic && IsLinkSetName(generic)))
                yield return node;
        }

        foreach (var (node, sql) in SqlLiterals.In(tree))
        {
            if (LinkRead().IsMatch(sql))
                yield return node;
        }
    }

    [Theory]
    [InlineData("class C { void M(LinkedRecordEntity l) => l.IsPrimary = true; }", Primary)]
    [InlineData("class C { object M() => new LinkedRecordEntity { RecordId = id }; }", Add)]
    [InlineData("class C { object M() => new LinkedRecordEntity { IsPrimary = true }; }", Add)]
    [InlineData("class C { object M() => new LinkedRecord { IsPrimary = true }; }", null)]
    [InlineData("class C { Task M(Db db) => db.LinkedRecords.ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, true)); }", Primary)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity l) => db.LinkedRecords.Add(l); }", Add)]
    [InlineData("class C { void M(Db db, List<LinkedRecordEntity> l) => db.LinkedRecords.AddRange(l); }", Add)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity l) => db.LinkedRecords.Remove(l); }", Delete)]
    [InlineData("class C { void M(Db db, List<LinkedRecordEntity> l) => db.LinkedRecords.RemoveRange(l); }", Delete)]
    [InlineData("class C { Task M(Db db) => db.LinkedRecords.Where(l => l.RecordId == id).ExecuteDeleteAsync(); }", Delete)]
    [InlineData("class C { Task M(Db db) => Unlinked(db).ExecuteDeleteAsync(); IQueryable<LinkedRecordEntity> Unlinked(Db db) => null; }", Delete)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity link) => db.Remove(link); }", Delete)]
    [InlineData("class C { void M(Db db, List<LinkedRecordEntity> links) => db.RemoveRange(links); }", Delete)]
    [InlineData("class C { void M(Db db) { foreach (var link in db.LinkedRecords.ToList()) db.Remove(link); } }", Delete)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity link) { db.Attach(link); db.Entry(link).State = EntityState.Deleted; } }", Add, Delete)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity link) => db.Entry(link).State = EntityState.Modified; }", Primary)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity link) => db.Entry(link).State = EntityState.Added; }", Add)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity link) => db.Set<LinkedRecordEntity>().Add(link); }", Add)]
    [InlineData("class C { void M(Db db, LinkedRecordEntity link) => db.Set<LinkedRecordEntity>().Remove(link); }", Delete)]
    [InlineData("class C { Task M(Db db) => db.Set<LinkedRecordEntity>().ExecuteDeleteAsync(); }", Delete)]
    [InlineData("class C { Task M(Db db) => db.Set<LinkedRecordEntity>().ExecuteUpdateAsync(s => s.SetProperty(l => l.CanonicalId, id)); }", Primary)]
    [InlineData("class C { string Sql => \"DELETE FROM public.linked_records WHERE id = @id\"; }", Delete)]
    [InlineData("class C { string Sql => \"INSERT INTO \\\"linked_records\\\" (id) VALUES (@id)\"; }", Add)]
    [InlineData("class C { string Sql => \"UPDATE \\\"public\\\".\\\"linked_records\\\" SET canonical_id = @c\"; }", Primary)]
    [InlineData("class C { void M(Db db, NoteEntity note) => db.Remove(note); }", null)]
    [InlineData("class C { void M(PatientInsulin i) => i.IsPrimary = true; }", null)]
    [InlineData("class C { Task M(Db db) => db.Notes.Where(n => n.Text == null).ExecuteDeleteAsync(); }", null)]
    [InlineData("class C { bool M(LinkedRecordEntity l) => l.IsPrimary; }", null)]
    [InlineData("class C { void M(SensorGlucoseEntity g) => g.IsDuplicate = true; }", Flag)]
    [InlineData("class C { object M() => new NoteEntity { IsDuplicate = true }; }", Flag)]
    [InlineData("class C { Task M(Db db) => db.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => n.IsDuplicate, false)); }", Flag)]
    [InlineData("class C { object M(Db db) => db.Notes.Where(n => !n.IsDuplicate); }", null)]
    [InlineData("class C { string Sql => \"UPDATE linked_records SET canonical_id = @c\"; }", Primary)]
    [InlineData("class C { string Sql => \"INSERT INTO linked_records (id) VALUES (@id)\"; }", Add)]
    [InlineData("class C { string Sql => \"DELETE FROM linked_records WHERE record_id = @id\"; }", Delete)]
    [InlineData("class C { string Sql => \"UPDATE notes SET text = @t, is_duplicate = true WHERE id = @id\"; }", Flag)]
    [InlineData("class C { string Sql => \"SELECT 1 FROM notes WHERE NOT is_duplicate\"; }", null)]
    [InlineData("class C { string Sql => \"SELECT id FROM linked_records WHERE record_id = ANY(@ids) FOR UPDATE\"; }", null)]
    public void RecognisesAWrite(string source, params string?[] kinds) =>
        Writes(CSharpSyntaxTree.ParseText(source), source).Select(w => w.Kind).Distinct().Should()
            .Equal((kinds ?? []).OfType<string>());

    private static IEnumerable<(SyntaxNode Node, string Kind)> Writes(SyntaxTree tree, string text)
    {
        var namesLinks = text.Contains("LinkedRecord", StringComparison.Ordinal);
        bool Watched(string name) => name == "IsDuplicate" || (name == "IsPrimary" && namesLinks);
        static string KindOf(string name) => name == "IsDuplicate" ? Flag : Primary;

        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax { Type: var type } when Named(type) == "LinkedRecordEntity":
                    yield return (node, Add);
                    break;
                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "IsDuplicate" } }
                    when node.Parent is InitializerExpressionSyntax:
                    yield return (node, Flag);
                    break;
                case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } }
                    when Watched(name):
                    yield return (node, KindOf(name));
                    break;
                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetProperty" },
                    ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax { Body: MemberAccessExpressionSyntax body } }, ..],
                } when Watched(body.Name.Identifier.ValueText):
                    yield return (node, KindOf(body.Name.Identifier.ValueText));
                    break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method } access } invocation
                    when namesLinks && LinkSetWrite(access.Expression, method, invocation.ArgumentList) is { } kind:
                    yield return (node, kind);
                    break;
                case AssignmentExpressionSyntax
                {
                    Left: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "State",
                        Expression: InvocationExpressionSyntax
                        {
                            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Entry" } or IdentifierNameSyntax { Identifier.ValueText: "Entry" },
                            ArgumentList.Arguments: [{ Expression: var entity }],
                        },
                    },
                    Right: var state,
                } when namesLinks && IsLink(entity) && EntryStateWrite(state) is { } kind:
                    yield return (node, kind);
                    break;
            }
        }

        foreach (var (node, sql) in SqlLiterals.In(tree))
        {
            if (LinkInsert().IsMatch(sql))
                yield return (node, Add);
            else if (LinkDelete().IsMatch(sql))
                yield return (node, Delete);
            else if (LinkUpdate().IsMatch(sql))
                yield return (node, Primary);
            else if (FlagWrite().IsMatch(sql))
                yield return (node, Flag);
        }
    }

    /// <summary>
    /// A bulk delete counts on any query in a source that names links, since a helper returning a
    /// query of links does not say so at the call. A tracked add, attach, update or remove counts on
    /// the link set (<c>LinkedRecords</c>, <c>Set&lt;LinkedRecordEntity&gt;()</c>), and on any other
    /// receiver, the context itself included, when its argument is a link (<see cref="IsLink"/>).
    /// </summary>
    private static string? LinkSetWrite(ExpressionSyntax receiver, string method, ArgumentListSyntax arguments)
    {
        if (method is "ExecuteDelete" or "ExecuteDeleteAsync")
            return Delete;
        var kind = method switch
        {
            "Add" or "AddRange" or "AddAsync" or "AddRangeAsync" or "Attach" or "AttachRange" => Add,
            "Remove" or "RemoveRange" => Delete,
            "Update" or "UpdateRange" => Primary,
            "ExecuteUpdate" or "ExecuteUpdateAsync" when IsLinkSet(receiver) => Primary,
            _ => null,
        };
        if (kind is null)
            return null;
        return IsLinkSet(receiver) || arguments.Arguments.Any(a => IsLink(a.Expression)) ? kind : null;
    }

    /// <summary>
    /// Whether a query chain starts from the link set: <c>LinkedRecords</c> or
    /// <c>Set&lt;LinkedRecordEntity&gt;()</c> somewhere along its receivers, not in a lambda passed to it.
    /// </summary>
    private static bool IsLinkSet(ExpressionSyntax expression)
    {
        for (var current = expression; ;)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                case MemberAccessExpressionSyntax { Name: var name } access:
                    if (IsLinkSetName(name))
                        return true;
                    current = access.Expression;
                    break;
                case SimpleNameSyntax name:
                    return IsLinkSetName(name);
                default:
                    return false;
            }
        }
    }

    private static bool IsLinkSetName(SimpleNameSyntax name) => name switch
    {
        IdentifierNameSyntax { Identifier.ValueText: "LinkedRecords" } => true,
        GenericNameSyntax { Identifier.ValueText: "Set", TypeArgumentList.Arguments: [var type] } => type.ToString() == "LinkedRecordEntity",
        _ => false,
    };

    private static string? EntryStateWrite(ExpressionSyntax state) => state.ToString() switch
    {
        var s when s.EndsWith("Deleted", StringComparison.Ordinal) => Delete,
        var s when s.EndsWith("Added", StringComparison.Ordinal) => Add,
        var s when s.EndsWith("Modified", StringComparison.Ordinal) => Primary,
        _ => null,
    };

    /// <summary>
    /// Whether an expression is a link or a set of links, read from the text with no semantic model:
    /// a <c>new LinkedRecordEntity</c>, an expression naming the link set, or an identifier the
    /// enclosing member declares (a parameter, a local, a <c>foreach</c> variable or an <c>out var</c>)
    /// with a type naming <c>LinkedRecord</c>, or as <c>var</c> from an expression naming it. A link
    /// reached through a field or a property is not seen.
    /// </summary>
    private static bool IsLink(ExpressionSyntax expression)
    {
        if (expression is ObjectCreationExpressionSyntax { Type: var type } && Named(type) == "LinkedRecordEntity")
            return true;
        if (IsLinkSet(expression))
            return true;
        if (expression is not IdentifierNameSyntax { Identifier.ValueText: var name })
            return false;

        var member = expression.Ancestors().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax);
        if (member is null)
            return false;

        static bool NamesLink(SyntaxNode? node) => node?.ToString().Contains("LinkedRecord", StringComparison.Ordinal) == true;

        return member.DescendantNodesAndSelf().Any(node => node switch
        {
            ParameterSyntax p => p.Identifier.ValueText == name && NamesLink(p.Type),
            VariableDeclaratorSyntax v => v.Identifier.ValueText == name
                && v.Parent is VariableDeclarationSyntax d
                && (d.Type.IsVar ? NamesLink(v.Initializer) : NamesLink(d.Type)),
            ForEachStatementSyntax f => f.Identifier.ValueText == name
                && (f.Type.IsVar ? NamesLink(f.Expression) : NamesLink(f.Type)),
            SingleVariableDesignationSyntax o => o.Identifier.ValueText == name
                && o.Parent is DeclarationExpressionSyntax { Type: var t }
                && (t.IsVar ? NamesLink(o.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault()) : NamesLink(t)),
            _ => false,
        });
    }

    private static string Named(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => string.Empty,
    };

    [GeneratedRegex(@"\binsert\s+into\s+(""?public""?\.)?""?linked_records\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkInsert();

    [GeneratedRegex(@"\bdelete\s+from\s+(""?public""?\.)?""?linked_records\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkDelete();

    [GeneratedRegex(@"\bupdate\s+(""?public""?\.)?""?linked_records\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkUpdate();

    [GeneratedRegex(@"\bset\b[^;]*\bis_duplicate\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex FlagWrite();

    [GeneratedRegex(@"(?<!\bdelete\s+)\b(from|join)\s+(""?public""?\.)?""?linked_records\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRead();
}
