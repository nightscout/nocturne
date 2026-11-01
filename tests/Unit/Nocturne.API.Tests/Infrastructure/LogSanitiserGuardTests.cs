using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.API.Helpers;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The API has one log sanitiser, <see cref="LogText.Sanitize"/>. A second one under another
/// definition of "safe" lets a value reach a log line with a character the first one folds away.
/// A method or local function whose name ends in <c>ForLog</c> is such a second one.
/// </summary>
[Trait("Category", "Unit")]
public class LogSanitiserGuardTests
{
    private const string Suffix = "ForLog";

    [Fact]
    public void The_API_declares_no_private_log_sanitiser()
    {
        var declared = RepoSources.Under("src/API")
            .SelectMany(file => Declarations(file.Tree).Select(line => $"{file.Path}:{line}"));

        declared.Should().BeEmpty($"these declare a method ending in {Suffix}; call {nameof(LogText)}.{nameof(LogText.Sanitize)} instead");
    }

    [Theory]
    [InlineData("class C { private static string SanitizeForLog(string v) => v; }", 1)]
    [InlineData("class C { static string? SanitizeForLog(string? v, int max = 200) => v; }", 1)]
    [InlineData("class C { void M() { static string SanitizeForLog(string v) => v; } }", 1)]
    [InlineData("static class C { public static string ForLog(string value) => value; }", 1)]
    [InlineData("class C { string TruncateForLogging(string v) => v; }", 0)]
    [InlineData("class C { string ForLogging(string v) => v; }", 0)]
    [InlineData("class C { void M(string v) { SanitizeForLog(v); ForLog(v); } }", 0)]
    [InlineData("class C { void M(string v) { LogText.Sanitize(v); } }", 0)]
    public void The_guard_finds_a_declaration_and_not_a_call(string source, int expected) =>
        Declarations(CSharpSyntaxTree.ParseText(source)).Should().HaveCount(expected);

    private static IEnumerable<int> Declarations(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .Where(node => node is MethodDeclarationSyntax method && method.Identifier.ValueText.EndsWith(Suffix, StringComparison.Ordinal)
                || node is LocalFunctionStatementSyntax local && local.Identifier.ValueText.EndsWith(Suffix, StringComparison.Ordinal))
            .Select(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
}
