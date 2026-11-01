using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>HistoryClampLiftExtensions.WriteWithHistoryClampLiftedAsync</c> runs a statement as an
/// unclamped reader would, so it is limited to the members listed here; its remarks say what a lifted
/// write may reach. A new caller is added here deliberately or not at all.
/// </summary>
/// <remarks>
/// The <c>NocturneDbContext.SaveChangesAsync</c> entry is not one write but all of them: every save
/// by a clamped reader that only inserts runs lifted, wherever it is made. That is safe while an
/// insert can return nothing but the row it wrote, which <see cref="HistoryClampLiftPremiseTests"/>
/// holds: no <c>ON CONFLICT</c> (which would update and return an existing row), no trigger, and no
/// column default but a literal or the clock.
/// </remarks>
[Trait("Category", "Unit")]
public class HistoryClampLiftAllowlistTests
{
    private const string Method = "WriteWithHistoryClampLiftedAsync";

    private static readonly string[] Allowed =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs: NocturneDbContext.SaveChangesAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/StateSpanSupersessionExtensions.cs: StateSpanSupersessionExtensions.ClearSupersededByAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TrackerRepository.cs: TrackerRepository.CompleteInstanceAsync",
    ];

    private static readonly IReadOnlyList<string> Callers = Scan();

    [Fact]
    public void OnlyTheAllowlistLiftsTheClamp()
    {
        Callers.Except(Allowed).Should().BeEmpty(
            $"{Method} lifts the member history clamp; allowlist a new caller here deliberately");
    }

    [Fact]
    public void EveryAllowlistEntryStillCallsIt()
    {
        Allowed.Except(Callers).Should().BeEmpty("a stale entry would silently cover a future call in that member");
    }

    private static List<string> Scan()
    {
        var callers = new List<string>();

        foreach (var source in RepoSources.Src)
        {
            if (!source.Text.Contains(Method, StringComparison.Ordinal))
                continue;

            var file = source.Path;
            callers.AddRange(source.Tree.GetRoot()
                .DescendantNodes()
                .Where(node => node switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == Method,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText == Method,
                    IdentifierNameSyntax name => name.Identifier.ValueText == Method
                        && name.Parent is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax),
                    _ => false,
                })
                .Select(EnclosingMember.Of)
                .Distinct()
                .Select(member => $"{file}: {member}"));
        }

        return callers;
    }
}
