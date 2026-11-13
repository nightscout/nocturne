using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Code lifts only the soft-delete filter, through <see cref="HistoryPage.IncludingDeleted{T}"/>. Any
/// call with no key or with the tenant key also drops tenant isolation, leaving it to a hand-written
/// predicate with Row Level Security as the only backstop. Outside the API host a call naming exactly
/// <see cref="NocturneDbContext.SoftDeleteFilterKey"/> is allowed (<c>IncludingDeleted</c> itself and
/// the keyed purge helpers), and so is any call in a member listed with the reason it reads past its
/// context's tenant. No code under <c>src/API</c> calls <c>IgnoreQueryFilters</c> in any shape.
/// Migrations are exempt.
/// </summary>
[Trait("Category", "Unit")]
public class IgnoreQueryFiltersGuardTests
{
    private const string Data = "src/Infrastructure/Nocturne.Infrastructure.Data";
    private const string Migrations = "src/Infrastructure/Nocturne.Infrastructure.Data.Migrations/";

    private static readonly Dictionary<string, string> TenantLifts = new()
    {
        [$"{Data}/Repositories/SubjectRepository.cs: SubjectRepository.ScrubTenantAuditTrailsAsync"] =
            "scrubs the deleted subject from every tenant's audit trail, naming each tenant in turn",
    };

    private static IEnumerable<SourceFile> OutsideTheApi => RepoSources.Src
        .Where(s => !s.Path.StartsWith("src/API/", StringComparison.Ordinal)
                    && !s.Path.StartsWith(Migrations, StringComparison.Ordinal));

    [Fact]
    public void NoApiCodeCallsIgnoreQueryFilters()
    {
        RepoSources.Under("src/API")
            .SelectMany(s => Calls(s.Tree).Select(n => $"{s.Path}: {EnclosingMember.Of(n)}"))
            .Should().BeEmpty("IgnoreQueryFilters can drop tenant isolation; use IncludingDeleted()");
    }

    [Fact]
    public void OnlyTheListedMembersLiftMoreThanTheSoftDeleteFilter()
    {
        Unlisted(OutsideTheApi, TenantLifts.Keys).Should().BeEmpty(
            "a lift of the tenant filter rests on a hand-written predicate; use IncludingDeleted()");
    }

    [Fact]
    public void EveryListedMemberStillLiftsTheTenantFilter()
    {
        Stale(OutsideTheApi, TenantLifts.Keys).Should().BeEmpty(
            "a stale entry would silently cover a future lift in that member");
    }

    [Theory]
    [InlineData("class C { void M(DbContext db) { db.Set<T>().IgnoreQueryFilters(); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q.IgnoreQueryFilters([NocturneDbContext.TenantFilterKey]); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q.IgnoreQueryFilters([\"tenant_isolation\"]); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q.IgnoreQueryFilters(new[] { NocturneDbContext.SoftDeleteFilterKey, NocturneDbContext.TenantFilterKey }); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q?.IgnoreQueryFilters(); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { EntityFrameworkQueryableExtensions.IgnoreQueryFilters(q); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q.IgnoreQueryFilters<T>(); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { IgnoreQueryFilters(q); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q\n    .Where(e => e.Id == id)\n    .IgnoreQueryFilters(\n    ); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { Func<IQueryable<T>> f = () => q.IgnoreQueryFilters(); } }", true)]
    [InlineData("class C { void M(IQueryable<T> q) { q.IncludingDeleted(); } }", false)]
    public void DetectsAnyCall(string source, bool expected) =>
        Calls(CSharpSyntaxTree.ParseText(source)).Any().Should().Be(expected);

    [Theory]
    [InlineData("q.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])", true)]
    [InlineData("q.IgnoreQueryFilters([SoftDeleteFilterKey])", true)]
    [InlineData("q.IgnoreQueryFilters(new[] { NocturneDbContext.SoftDeleteFilterKey })", true)]
    [InlineData("q.IgnoreQueryFilters(new string[] { NocturneDbContext.SoftDeleteFilterKey })", true)]
    [InlineData("EntityFrameworkQueryableExtensions.IgnoreQueryFilters(q, [NocturneDbContext.SoftDeleteFilterKey])", true)]
    [InlineData("q.IgnoreQueryFilters()", false)]
    [InlineData("q?.IgnoreQueryFilters()", false)]
    [InlineData("EntityFrameworkQueryableExtensions.IgnoreQueryFilters(q)", false)]
    [InlineData("q.IgnoreQueryFilters([NocturneDbContext.TenantFilterKey])", false)]
    [InlineData("q.IgnoreQueryFilters([\"soft_delete\"])", false)]
    [InlineData("q.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey, NocturneDbContext.TenantFilterKey])", false)]
    [InlineData("q.IgnoreQueryFilters(new[] { NocturneDbContext.SoftDeleteFilterKey, NocturneDbContext.TenantFilterKey })", false)]
    [InlineData("q.IgnoreQueryFilters(keys)", false)]
    [InlineData("q.IgnoreQueryFilters([])", false)]
    [InlineData("q.IgnoreQueryFilters([Other.SoftDeleteFilterKey])", false)]
    public void AllowsOnlyTheSoftDeleteKeyAlone(string call, bool allowed)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ object M(IQueryable<T> q) => {call}; }}");

        Calls(tree).Should().ContainSingle().Which.Should().Match<InvocationExpressionSyntax>(
            c => LiftsOnlySoftDelete(c) == allowed);
    }

    [Fact]
    public void AnUnlistedMemberLiftingTheTenantFilterIsReported()
    {
        var sources = Sources(
            "class Repo { IQueryable<T> Kept(IQueryable<T> q) => q.IncludingDeleted(); "
            + "IQueryable<T> Keyed(IQueryable<T> q) => q.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]); "
            + "IQueryable<T> Bare(IQueryable<T> q) => q.IgnoreQueryFilters(); "
            + "IQueryable<T> Listed(IQueryable<T> q) => q.IgnoreQueryFilters(); }");

        Unlisted(sources, [$"{Data}/Repo.cs: Repo.Listed"]).Should().Equal($"{Data}/Repo.cs: Repo.Bare");
    }

    [Fact]
    public void AListedMemberThatLiftsOnlyTheSoftDeleteFilterIsStale()
    {
        var sources = Sources(
            "class Repo { IQueryable<T> Kept(IQueryable<T> q) => q.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]); "
            + "IQueryable<T> Lifts(IQueryable<T> q) => q.IgnoreQueryFilters(); }");

        Stale(sources, [$"{Data}/Repo.cs: Repo.Kept", $"{Data}/Repo.cs: Repo.Lifts", $"{Data}/Repo.cs: Repo.Gone"])
            .Should().Equal($"{Data}/Repo.cs: Repo.Kept", $"{Data}/Repo.cs: Repo.Gone");
    }

    private static IEnumerable<SourceFile> Sources(string code) => [new SourceFile($"{Data}/Repo.cs", code)];

    private static List<string> TenantLiftingMembers(IEnumerable<SourceFile> sources) => sources
        .SelectMany(s => Calls(s.Tree)
            .Where(call => !LiftsOnlySoftDelete(call))
            .Select(call => $"{s.Path}: {EnclosingMember.Of(call)}"))
        .Distinct()
        .ToList();

    private static List<string> Unlisted(IEnumerable<SourceFile> sources, IEnumerable<string> listed) =>
        TenantLiftingMembers(sources).Except(listed).ToList();

    private static List<string> Stale(IEnumerable<SourceFile> sources, IEnumerable<string> listed) =>
        listed.Except(TenantLiftingMembers(sources)).ToList();

    private static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => SimpleName(call.Expression) == "IgnoreQueryFilters");

    /// <summary>
    /// Whether the call's last argument, its key list in either the extension or the static form, is a
    /// collection holding <see cref="NocturneDbContext.SoftDeleteFilterKey"/> and nothing else.
    /// </summary>
    private static bool LiftsOnlySoftDelete(InvocationExpressionSyntax call)
    {
        if (call.ArgumentList.Arguments.LastOrDefault()?.Expression is not { } keys)
            return false;

        IEnumerable<ExpressionSyntax>? elements = keys switch
        {
            CollectionExpressionSyntax collection when collection.Elements.All(e => e is ExpressionElementSyntax) =>
                collection.Elements.Cast<ExpressionElementSyntax>().Select(e => e.Expression),
            ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions,
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
            _ => null,
        };

        return elements?.ToList() is [var only] && IsTheSoftDeleteKey(only);
    }

    /// <summary>The key by its own name, bare or qualified by <see cref="NocturneDbContext"/> and by nothing else.</summary>
    private static bool IsTheSoftDeleteKey(ExpressionSyntax key) => key switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText == nameof(NocturneDbContext.SoftDeleteFilterKey),
        MemberAccessExpressionSyntax { Expression: var owner, Name.Identifier.ValueText: nameof(NocturneDbContext.SoftDeleteFilterKey) } =>
            SimpleName(owner) == nameof(NocturneDbContext),
        _ => false,
    };

    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };
}
