using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The API host lifts only the soft-delete filter, through <see cref="HistoryPage.IncludingDeleted{T}"/>,
/// so no code under <c>src/API</c> calls <c>IgnoreQueryFilters</c> in any shape. Any call with no key
/// or with the tenant key also drops tenant isolation, leaving it to a hand-written predicate with Row
/// Level Security as the only backstop.
/// </summary>
[Trait("Category", "Unit")]
public class IgnoreQueryFiltersGuardTests
{
    [Fact]
    public void NoApiCodeCallsIgnoreQueryFilters()
    {
        RepoSources.Under("src/API")
            .SelectMany(s => Calls(s.Tree).Select(n => $"{s.Path}: {EnclosingMember.Of(n)}"))
            .Should().BeEmpty("IgnoreQueryFilters can drop tenant isolation; use IncludingDeleted()");
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

    private static IEnumerable<SyntaxNode> Calls(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => InvokedName(call.Expression) == "IgnoreQueryFilters");

    private static string? InvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };
}
