using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <see cref="DecomposedTreatmentTables"/> runs audited soft deletes over the treatment tables on a
/// context its caller opened. It is public for the treatment decomposer's delete paths alone, so no
/// other file of the host names it in code.
/// </summary>
[Trait("Category", "Unit")]
public class DecomposedTreatmentTablesGuardTests
{
    private const string Allowed = "src/API/Nocturne.API/Services/V4/TreatmentDecomposer.cs";

    [Fact]
    public void OnlyTheTreatmentDecomposerNamesTheTablesInTheHost()
    {
        RepoSources.Under("src/API")
            .Where(source => source.Path != Allowed && Names(source.Tree))
            .Select(source => source.Path)
            .Should().BeEmpty("the treatment tables are reached through the treatment module's service");
    }

    [Fact]
    public void TheTreatmentDecomposerStillNamesThem() =>
        Names(RepoSources.Under("src/API").Single(source => source.Path == Allowed).Tree)
            .Should().BeTrue("the allowance outlives the use it excuses");

    [Theory]
    [InlineData("class C { void M() => DecomposedTreatmentTables.All.Count(); }", true)]
    [InlineData("using T = Nocturne.Infrastructure.Data.Repositories.V4.DecomposedTreatmentTables; class C { }", true)]
    [InlineData("/// <see cref=\"DecomposedTreatmentTables\"/>\nclass C { string S = \"DecomposedTreatmentTables\"; }", false)]
    public void FindsANameInCodeOnly(string source, bool expected) =>
        Names(CSharpSyntaxTree.ParseText(source)).Should().Be(expected);

    private static bool Names(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
            .Any(name => name.Identifier.ValueText == nameof(DecomposedTreatmentTables));
}
