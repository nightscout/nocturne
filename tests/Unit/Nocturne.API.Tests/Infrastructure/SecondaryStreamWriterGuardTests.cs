using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>sensor_glucose.is_secondary_stream</c> holds the outcome of the stream rule over each bucket's
/// visible readings, and <c>GlucoseStreamFlags</c> recomputes it whole buckets at a time under the
/// tenant's stream lock. Any other write could set one reading's flag without the rest of its bucket,
/// so only that module's statement may write it.
/// </summary>
/// <remarks>
/// Syntax only: an assignment or initializer of <c>IsSecondaryStream</c>, an <c>ExecuteUpdate</c>
/// setter selecting it, or SQL setting <c>is_secondary_stream</c>. Migrations are exempt: they add
/// the column and leave the values to the recompute job.
/// </remarks>
[Trait("Category", "Unit")]
public partial class SecondaryStreamWriterGuardTests
{
    private static readonly string[] Writers =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/GlucoseStreamFlags.cs: GlucoseStreamFlags.WriteAsync",
    ];

    private static readonly IReadOnlyList<string> Found = RepoSources.Src
        .Where(s => !s.Path.StartsWith("src/Infrastructure/Nocturne.Infrastructure.Data.Migrations/", StringComparison.Ordinal))
        .Where(s => s.Text.Contains("IsSecondaryStream", StringComparison.Ordinal)
            || s.Text.Contains("is_secondary_stream", StringComparison.Ordinal))
        .SelectMany(s => Writes(s.Tree).Select(node => $"{s.Path}: {EnclosingMember.Of(node)}"))
        .Distinct()
        .ToList();

    [Fact]
    public void OnlyTheStreamModuleWritesTheFlag()
    {
        Found.Except(Writers).Should().BeEmpty(
            "a stream flag written outside GlucoseStreamFlags can leave a bucket showing two streams or none");
    }

    [Fact]
    public void TheListedWriterStillWrites()
    {
        Writers.Except(Found).Should().BeEmpty("a stale entry would cover a future writer in that member");
    }

    [Theory]
    [InlineData("class C { void M(SensorGlucoseEntity g) => g.IsSecondaryStream = true; }", true)]
    [InlineData("class C { object M() => new SensorGlucoseEntity { IsSecondaryStream = true }; }", true)]
    [InlineData("class C { Task M(Db db) => db.SensorGlucose.ExecuteUpdateAsync(s => s.SetProperty(e => e.IsSecondaryStream, true)); }", true)]
    [InlineData("class C { string Sql => \"UPDATE sensor_glucose SET mgdl = 1, is_secondary_stream = true\"; }", true)]
    [InlineData("class C { object M(Db db) => db.SensorGlucose.Where(e => !e.IsSecondaryStream); }", false)]
    [InlineData("class C { string Sql => \"SELECT id FROM sensor_glucose WHERE NOT is_secondary_stream\"; }", false)]
    [InlineData("class C { void M(SensorGlucoseEntity g) => g.IsDuplicate = true; }", false)]
    public void RecognisesAWrite(string source, bool write) =>
        Writes(CSharpSyntaxTree.ParseText(source)).Any().Should().Be(write);

    private static IEnumerable<SyntaxNode> Writes(SyntaxTree tree)
    {
        const string Flag = "IsSecondaryStream";
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: Flag } }
                    when node.Parent is InitializerExpressionSyntax:
                case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: Flag } }:
                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetProperty" },
                    ArgumentList.Arguments: [{ Expression: LambdaExpressionSyntax { Body: MemberAccessExpressionSyntax { Name.Identifier.ValueText: Flag } } }, ..],
                }:
                    yield return node;
                    break;
            }
        }

        foreach (var (node, sql) in SqlLiterals.In(tree))
        {
            if (FlagWrite().IsMatch(sql))
                yield return node;
        }
    }

    [GeneratedRegex(@"\bset\b[^;]*\bis_secondary_stream\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex FlagWrite();
}
