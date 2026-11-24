using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Outside <c>MembershipReasons</c>, nothing under <c>src</c> adds, removes, attaches, updates or
/// bulk-writes a member row, its role links or its reasons, through a set, <c>Set&lt;T&gt;()</c>, the
/// context, a navigation or <c>Entry(...).State</c>; names their tables in SQL text; or assigns their
/// columns. A member row is the stored sum of its reasons, so a write anywhere else would leave the
/// two apart. Only the dev snapshot import names <c>DevSnapshotMemberships</c>, which skips the owners
/// lock, the last-owner check and the audit of what a membership holds.
/// </summary>
/// <remarks>
/// A syntax tree has no types, so an entity value is one created inline (explicitly, or target-typed
/// beside a generic type argument), or a name the same member declares with an entity type, creates
/// as one, or reads from one of the sets; and a column assignment is matched by name, through a
/// navigation to the member or reason, or inside a member that names a set or an entity. Deletions by
/// foreign-key cascade are not writes this guard sees: deleting a tenant (<c>TenantRepository</c>) or a
/// role deletes the rows that hang off it in the database, and each member's reasons and their roles
/// cascade with it, so nothing is left to disagree. Migrations
/// are not scanned: they run once, before any writer, and the backfill is the one that writes reasons.
/// </remarks>
[Trait("Category", "Unit")]
public partial class MembershipWriteGuardTests
{
    private const string Writer = "src/Infrastructure/Nocturne.Infrastructure.Data/Services/MembershipReasons.cs";
    private const string DevPassThrough = "src/Infrastructure/Nocturne.Infrastructure.Data/Services/DevSnapshotMemberships.cs";
    private const string DevImport = "src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs";

    private static readonly string[] Sets = ["TenantMembers", "TenantMemberRoles", "TenantMemberReasons", "TenantMemberReasonRoles"];

    private static readonly string[] Entities =
        ["TenantMemberEntity", "TenantMemberRoleEntity", "TenantMemberReasonEntity", "TenantMemberReasonRoleEntity"];

    /// <summary>
    /// The collection navigations that hold the four entities: <c>TenantEntity.Members</c>,
    /// <c>MemberInviteEntity.CreatedMembers</c>, <c>TenantMemberEntity.MemberRoles</c>,
    /// <c>TenantRoleEntity.MemberRoles</c> and <c>TenantMemberReasonEntity.ReasonRoles</c>.
    /// </summary>
    private static readonly string[] CollectionNavigations = ["Members", "CreatedMembers", "MemberRoles", "ReasonRoles"];

    /// <summary>The reference navigations to a member or a reason: <c>TenantMember</c> and <c>TenantMemberReason</c>.</summary>
    private static readonly string[] ReferenceNavigations = ["TenantMember", "TenantMemberReason"];

    private static readonly string[] WriteMethods =
        ["Add", "AddAsync", "AddRange", "AddRangeAsync", "Remove", "RemoveRange", "Attach", "AttachRange", "Update", "UpdateRange"];

    private static readonly string[] CollectionWriteMethods =
        ["Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAll", "RemoveAt", "RemoveRange", "Clear"];

    private static readonly string[] BulkMethods =
        ["ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync"];

    private static readonly string[] Columns =
    [
        "Id", "TenantId", "SubjectId", "DirectPermissions", "Label", "Username", "LimitTo24Hours", "AccessEndsAt",
        "CreatedFromInviteId", "LastUsedAt", "LastUsedIp", "LastUsedUserAgent", "TenantMemberId", "TenantRoleId",
        "TenantMemberReasonId", "Kind", "SourceId", "NextReasonLapseAt",
    ];

    /// <summary>
    /// The writes of row-only state, which no reason carries. An entry names the file and the member
    /// the write sits in, so a new write in a listed file is not covered by its neighbour.
    /// </summary>
    private static readonly string[] Allowed =
    [
        // The per-tenant display name is the row's own, not access.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TenantMembershipRepository.cs: TenantMembershipRepository.SetUsernameAsync",
        // The last-used stamp outlives the request in one statement, and is not access.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TenantMembershipRepository.cs: TenantMembershipRepository.RecordUseAsync",
        // Setup adoption gives the adopting subject the created subject's username: a subject column of the
        // same name, matched by name. The membership itself passes through the writer, and
        // NothingMovesAMembershipToAnotherSubjectOrTenant keeps it from being re-pointed here.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.AdoptForSetupAsync",
    ];

    /// <summary>The columns that say whose membership a row is and of which tenant.</summary>
    private static readonly string[] MembershipKeys = ["SubjectId", "TenantId"];

    private static readonly IReadOnlyList<(string Path, SyntaxTree Tree)> Sources = Load();

    [Fact]
    public void OnlyTheWriterWritesAMembership()
    {
        var writers = Sources
            .SelectMany(s => WritingMembers(s.Tree).Select(member => $"{s.Path}: {member}"))
            .Distinct()
            .ToList();

        writers.Except(Allowed).Should().BeEmpty(
            "a membership is the stored sum of its reasons; change it through MembershipReasons");
        Allowed.Except(writers).Should().BeEmpty("a stale entry would silently cover a future write in that member");
    }

    /// <summary>
    /// A membership moved to another subject or tenant ends one membership and starts another, which
    /// only the writer records, so no entry in <see cref="Allowed"/> may cover it.
    /// </summary>
    [Fact]
    public void NothingMovesAMembershipToAnotherSubjectOrTenant() =>
        Sources
            .SelectMany(s => s.Tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(IsMembershipKeyAssignment)
                .Select(a => $"{s.Path}: {EnclosingMember.Of(a)}"))
            .Should().BeEmpty("a membership that changes hands is removed and added through MembershipReasons");

    [Theory]
    [InlineData("class C { async Task M(Db db) { var ms = await db.TenantMembers.ToListAsync(); ms[0].SubjectId = h; } }", true)]
    [InlineData("class C { async Task M(Db db) { var m = await db.TenantMembers.FirstAsync(); m.TenantId = t; } }", true)]
    [InlineData("class C { void M(TenantMemberEntity m) { m.SubjectId = h; } }", true)]
    [InlineData("class C { void M(Holder h) { h.TenantMember.SubjectId = s; } }", true)]
    [InlineData("class C { void M(Db db, SubjectRoleEntity r) { db.TenantMembers.Load(); r.SubjectId = h; } }", false)]
    [InlineData("class C { object M() => new TenantMemberEntity { SubjectId = h }; }", false)]
    [InlineData("class C { async Task M(Db db) { var m = await db.TenantMembers.FirstAsync(); m.Label = l; } }", false)]
    public void DetectsAMembershipMoved(string source, bool expected) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(IsMembershipKeyAssignment).Should().Be(expected);

    private static bool IsMembershipKeyAssignment(AssignmentExpressionSyntax assignment) =>
        assignment.Parent is not InitializerExpressionSyntax
        && assignment.Left is MemberAccessExpressionSyntax left
        && MembershipKeys.Contains(left.Name.Identifier.ValueText)
        && (NameOf(left.Expression) is { } navigation && ReferenceNavigations.Contains(navigation)
            || IsEntityValue(Unindexed(left.Expression)));

    private static ExpressionSyntax Unindexed(ExpressionSyntax expression) =>
        expression is ElementAccessExpressionSyntax element ? Unindexed(element.Expression) : expression;

    [Fact]
    public void OnlyTheDevSnapshotImportNamesTheDevPassThrough() =>
        Sources
            .Where(s => s.Path != DevPassThrough && NamesDevPassThrough(s.Tree))
            .Select(s => s.Path)
            .Should().Equal([DevImport], "it skips the owners lock, the last-owner check and the audit of what a membership holds");

    [Fact]
    public void TheExemptFilesExist() =>
        RepoSources.Src.Select(s => s.Path).Should().Contain([Writer, DevPassThrough, DevImport], "the guard names them by path");

    [Theory]
    [InlineData("class C { void M(Db db) => db.TenantMembers.Add(new TenantMemberEntity()); }")]
    [InlineData("class C { void M(Db db, object m) => db.TenantMemberRoles.RemoveRange(m); }")]
    [InlineData("class C { void M(Db db, object r) => db.TenantMemberReasons.Remove(r); }")]
    [InlineData("class C { void M(Db db, object r) => db.TenantMemberReasonRoles.Attach(r); }")]
    [InlineData("class C { void M(Db db, object x) => db.Set<TenantMemberEntity>().Add(x); }")]
    [InlineData("class C { void M(Db db) => db.Set<TenantMemberRoleEntity>().Where(x => true).ExecuteDelete(); }")]
    [InlineData("class C { void M(Db db) => db.Add(new TenantMemberRoleEntity()); }")]
    [InlineData("class C { void M(Db db) => db.AddRange(new TenantMemberReasonEntity(), new TenantMemberReasonEntity()); }")]
    [InlineData("class C { void M(Db db) => db.Attach<TenantMemberEntity>(new() { Label = \"x\" }); }")]
    [InlineData("class C { void M(Db db, TenantMemberEntity m) => db.Update(m); }")]
    [InlineData("class C { void M(Db db) { var r = new TenantMemberReasonRoleEntity(); db.Remove(r); } }")]
    [InlineData("class C { void M(Db db) { TenantMemberEntity m = Find(); db.Remove(m); } }")]
    [InlineData("class C { async Task M(Db db) { var m = await db.TenantMembers.FirstAsync(); db.Remove(m); } }")]
    [InlineData("class C { async Task M(Db db) { var ms = await db.TenantMemberRoles.ToListAsync(); db.RemoveRange(ms); } }")]
    [InlineData("class C { void M(Tenant t) => t.Members.Add(new() { SubjectId = s }); }")]
    [InlineData("class C { void M(Invite i) => i.CreatedMembers.Add(new() { SubjectId = s }); }")]
    [InlineData("class C { void M(Member m, object link) => m.MemberRoles.Remove(link); }")]
    [InlineData("class C { void M(Member m) => m.MemberRoles.Clear(); }")]
    [InlineData("class C { void M(Role r) => r.MemberRoles.RemoveAll(x => true); }")]
    [InlineData("class C { void M(Reason r, object rr) => r.ReasonRoles.Add(rr); }")]
    [InlineData("class C { void M(Member m) { m.MemberRoles = []; } }")]
    [InlineData("class C { void M(Db db, TenantMemberEntity m) { db.Entry(m).State = EntityState.Deleted; } }")]
    [InlineData("class C { void M(Db db) { db.Entry<TenantMemberEntity>(x).State = EntityState.Added; } }")]
    [InlineData("class C { void M(Db db) => db.TenantMembers.Where(m => true).ExecuteDeleteAsync(); }")]
    [InlineData("class C { void M(Db db) => db.TenantMemberRoles.Where(m => true).ExecuteUpdate(s => s); }")]
    [InlineData("class C { void M(Db db) => db.Database.ExecuteSqlRaw(\"DELETE FROM tenant_members WHERE id = 1\"); }")]
    [InlineData("class C { string M() => $\"UPDATE tenant_member_reasons SET kind = '{k}'\"; }")]
    [InlineData("class C { string M() => \"\"\"\n    INSERT INTO tenant_member_roles VALUES (1)\n    \"\"\"; }")]
    [InlineData("class C { string M() => @\"SELECT * FROM tenant_member_reason_roles\"; }")]
    [InlineData("class C { void M(Holder h) { h.TenantMember.AccessEndsAt = null; } }")]
    [InlineData("class C { void M(Holder h) { h.TenantMemberReason.DirectPermissions = null; } }")]
    [InlineData("class C { async Task M(Db db) { var m = await db.TenantMembers.FirstAsync(); m.AccessEndsAt = null; } }")]
    [InlineData("class C { async Task M(Db db) { var m = await db.TenantMembers.FirstAsync(); m.SubjectId = x; } }")]
    [InlineData("class C { void M(TenantMemberEntity m) { m.DirectPermissions = null; } }")]
    public void FlagsAMembershipWrite(string source) =>
        WritingMembers(CSharpSyntaxTree.ParseText(source)).Should().NotBeEmpty();

    [Theory]
    [InlineData("class C { void M(Db db) => db.TenantMembers.Where(m => true).ToListAsync(); }")]
    [InlineData("class C { void M(Db db) { var m = await db.TenantMembers.FirstAsync(); m.SysUpdatedAt = now; } }")]
    [InlineData("class C { void M(Grant g) { g.LimitTo24Hours = true; } }")]
    [InlineData("class C { object M(TenantMemberEntity m) => new TenantMemberEntity { DirectPermissions = m.DirectPermissions }; }")]
    [InlineData("class C { object M(TenantRoleEntity r) => new TenantMemberEntity { MemberRoles = [new TenantMemberRoleEntity { TenantRole = r }] }; }")]
    [InlineData("class C { void M(Db db) => db.AlertRules.Add(new AlertRuleEntity()); }")]
    [InlineData("class C { async Task M(Db db) { var role = await db.TenantRoles.FirstAsync(); db.TenantRoles.Remove(role); } }")]
    [InlineData("[Table(\"tenant_members\")] class C { }")]
    [InlineData("class C { void M(B b) => b.HasDatabaseName(\"ix_tenant_members_subject_id\"); }")]
    [InlineData("/// <c>tenant_members</c>\nclass C { }")]
    [InlineData("class C { void M(Db db, TenantMemberEntity m) => MembershipReasons.RemoveMemberAsync(db, m, ct); }")]
    public void LeavesAReadOrAWriteThroughTheWriterAlone(string source) =>
        WritingMembers(CSharpSyntaxTree.ParseText(source)).Should().BeEmpty();

    [Theory]
    [InlineData("class C { void M(Db db) => DevSnapshotMemberships.Restore(db, m, g, null); }", true)]
    [InlineData("class C { void M() { } }", false)]
    public void DetectsTheDevPassThrough(string source, bool expected) =>
        NamesDevPassThrough(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    private static bool NamesDevPassThrough(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(name => name.Identifier.ValueText == "DevSnapshotMemberships");

    private static List<string> WritingMembers(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .Where(node => node switch
            {
                InvocationExpressionSyntax invocation =>
                    IsSetWrite(invocation) || IsBulkWrite(invocation) || IsEntityWrite(invocation) || IsNavigationWrite(invocation),
                AssignmentExpressionSyntax assignment =>
                    IsColumnAssignment(assignment) || IsNavigationAssignment(assignment) || IsStateAssignment(assignment),
                _ => false,
            })
            .Concat(SqlNaming(tree))
            .Select(EnclosingMember.Of)
            .Distinct()
            .ToList();

    private static bool IsSetWrite(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax access
        && WriteMethods.Contains(access.Name.Identifier.ValueText)
        && IsSet(access.Expression);

    private static bool IsSet(ExpressionSyntax receiver) => receiver switch
    {
        InvocationExpressionSyntax { Expression: var expression } => GenericOf(expression) is { Identifier.ValueText: "Set" } set
            && set.TypeArgumentList.Arguments.Any(IsEntityType),
        _ => NameOf(receiver) is { } name && Sets.Contains(name),
    };

    private static bool IsBulkWrite(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax access
        && BulkMethods.Contains(access.Name.Identifier.ValueText)
        && access.Expression.DescendantNodesAndSelf().Any(node => node switch
        {
            IdentifierNameSyntax name => Sets.Contains(name.Identifier.ValueText),
            GenericNameSyntax { Identifier.ValueText: "Set" } set => set.TypeArgumentList.Arguments.Any(IsEntityType),
            _ => false,
        });

    /// <summary>A context or set write on any receiver whose argument is an entity value, or whose type argument is an entity.</summary>
    private static bool IsEntityWrite(InvocationExpressionSyntax invocation) =>
        InvokedName(invocation) is { } name && WriteMethods.Contains(name)
        && ((GenericOf(invocation.Expression) is { } generic && generic.TypeArgumentList.Arguments.Any(IsEntityType))
            || invocation.ArgumentList.Arguments.Any(a => IsEntityValue(a.Expression)));

    private static bool IsNavigationWrite(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax access
        && CollectionWriteMethods.Contains(access.Name.Identifier.ValueText)
        && NameOf(access.Expression) is { } receiver
        && CollectionNavigations.Contains(receiver);

    private static bool IsNavigationAssignment(AssignmentExpressionSyntax assignment) =>
        assignment.Parent is not InitializerExpressionSyntax
        && assignment.Left is MemberAccessExpressionSyntax left
        && CollectionNavigations.Concat(ReferenceNavigations).Contains(left.Name.Identifier.ValueText);

    private static bool IsStateAssignment(AssignmentExpressionSyntax assignment) =>
        assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "State", Expression: InvocationExpressionSyntax entry }
        && InvokedName(entry) == "Entry"
        && ((GenericOf(entry.Expression) is { } generic && generic.TypeArgumentList.Arguments.Any(IsEntityType))
            || entry.ArgumentList.Arguments.Any(a => IsEntityValue(a.Expression)));

    /// <summary>
    /// An assignment statement (not an object initializer entry) to a column: through a navigation to
    /// a member or reason anywhere, or on any receiver in a member that names a set or an entity.
    /// </summary>
    private static bool IsColumnAssignment(AssignmentExpressionSyntax assignment) =>
        assignment.Parent is not InitializerExpressionSyntax
        && assignment.Left is MemberAccessExpressionSyntax left
        && Columns.Contains(left.Name.Identifier.ValueText)
        && ((NameOf(left.Expression) is { } receiver && ReferenceNavigations.Contains(receiver))
            || (EnclosingMemberOf(assignment) is { } member
                && member.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(name => Sets.Contains(name.Identifier.ValueText) || Entities.Contains(name.Identifier.ValueText))));

    /// <summary>
    /// An entity created inline, or a name the enclosing member declares as an entity, creates as one,
    /// or reads from a set.
    /// </summary>
    private static bool IsEntityValue(ExpressionSyntax expression) => expression switch
    {
        ObjectCreationExpressionSyntax creation => IsEntityType(creation.Type),
        IdentifierNameSyntax name => EnclosingMemberOf(expression) is { } member && EntityNames(member).Contains(name.Identifier.ValueText),
        _ => false,
    };

    private static HashSet<string> EntityNames(MemberDeclarationSyntax member)
    {
        var names = new HashSet<string>();
        foreach (var node in member.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParameterSyntax parameter when IsEntityType(parameter.Type):
                    names.Add(parameter.Identifier.ValueText);
                    break;
                case VariableDeclarationSyntax declaration:
                    foreach (var variable in declaration.Variables)
                    {
                        if (IsEntityType(declaration.Type) || (variable.Initializer?.Value is { } value && ProducesEntity(value)))
                            names.Add(variable.Identifier.ValueText);
                    }

                    break;
                case ForEachStatementSyntax loop when IsEntityType(loop.Type) || ProducesEntity(loop.Expression):
                    names.Add(loop.Identifier.ValueText);
                    break;
            }
        }

        return names;
    }

    private static bool ProducesEntity(ExpressionSyntax value) =>
        (value is ObjectCreationExpressionSyntax creation && IsEntityType(creation.Type))
        || value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(name => Sets.Contains(name.Identifier.ValueText));

    private static IEnumerable<SyntaxNode> SqlNaming(SyntaxTree tree) =>
        tree.GetRoot().DescendantTokens()
            .Where(token => token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                    or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                    or SyntaxKind.Utf8StringLiteralToken
                && Tables().IsMatch(token.ValueText.Length > 0 ? token.ValueText : token.Text)
                && token.Parent?.FirstAncestorOrSelf<AttributeSyntax>() is null)
            .Select(token => token.Parent!);

    [GeneratedRegex(@"\btenant_member(s|_roles|_reasons|_reason_roles)\b")]
    private static partial Regex Tables();

    private static MemberDeclarationSyntax? EnclosingMemberOf(SyntaxNode node) =>
        node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not BaseTypeDeclarationSyntax);

    private static bool IsEntityType(TypeSyntax? type) => type switch
    {
        NullableTypeSyntax nullable => IsEntityType(nullable.ElementType),
        GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Any(IsEntityType),
        ArrayTypeSyntax array => IsEntityType(array.ElementType),
        _ => type is not null && NameOf(type) is { } name && Entities.Contains(name),
    };

    private static GenericNameSyntax? GenericOf(ExpressionSyntax expression) => expression switch
    {
        GenericNameSyntax generic => generic,
        MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
        _ => null,
    };

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    private static string? NameOf(SyntaxNode node) => node switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        _ => null,
    };

    private static List<(string, SyntaxTree)> Load() =>
        RepoSources.Src
            .Where(s => s.Path != Writer && !s.Path.Contains("/Migrations/", StringComparison.Ordinal))
            .Select(s => (s.Path, s.Tree))
            .ToList();
}
