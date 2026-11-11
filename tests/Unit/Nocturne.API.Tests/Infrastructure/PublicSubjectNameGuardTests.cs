using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The Public system subject is named once, in <see cref="PublicSubjectFilter"/>. Source that spells
/// its name out again can drift from the filter, and then a share link's membership is edited as a
/// person's or a tenant is provisioned without it. No source outside the filter holds the exact
/// string, so a match on the literal is enough: longer text such as a label does not match.
/// </summary>
[Trait("Category", "Unit")]
public class PublicSubjectNameGuardTests
{
    private const string FilterPath =
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/PublicSubjectFilter.cs";

    [Fact]
    public void Source_outside_the_filter_does_not_spell_out_the_public_subject_name()
    {
        RepoSources.Src.Should().Contain(file => file.Path == FilterPath);

        var spellings = RepoSources.Src
            .Where(file => file.Path != FilterPath)
            .SelectMany(file => Spellings(file.Tree).Select(line => $"{file.Path}:{line}"))
            .ToList();

        spellings.Should().BeEmpty(
            $"use {nameof(PublicSubjectFilter)}.{nameof(PublicSubjectFilter.PublicSubjectName)} or its predicates");
    }

    [Fact]
    public void The_filter_holds_the_name_the_guard_looks_for()
    {
        PublicSubjectFilter.PublicSubjectName.Should().Be("Public");
    }

    [Theory]
    [InlineData("class C { bool M(string n) => n == \"Public\"; }", 1)]
    [InlineData("class C { bool M(string n) => \"Public\" != n; }", 1)]
    [InlineData("class C { bool M(R r) => r is { SubjectName: \"Public\" }; }", 1)]
    [InlineData("class C { const string Name = \"Public\"; }", 1)]
    [InlineData("class C { void M() { Find(\"Public\"); Find(name: \"Public\"); } }", 2)]
    [InlineData("class C { object M(string n) => n switch { \"Public\" => 1, _ => 2 }; }", 1)]
    [InlineData("class C { string M(string n) => n + \"Public Access\"; }", 0)]
    [InlineData("class C { string M() => \"public\"; }", 0)]
    [InlineData("class C { string M() => \"Publicly\"; }", 0)]
    [InlineData("class C { /* \"Public\" */ int M() => 1; } // == \"Public\"", 0)]
    public void The_guard_recognises_a_spelled_out_subject_name(string source, int expected)
    {
        Spellings(CSharpSyntaxTree.ParseText(source)).Should().HaveCount(expected);
    }

    private static IEnumerable<string> Spellings(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText == PublicSubjectFilter.PublicSubjectName)
            .Select(literal => $"{tree.GetLineSpan(literal.Span).StartLinePosition.Line + 1}: {literal.Parent}");
}
