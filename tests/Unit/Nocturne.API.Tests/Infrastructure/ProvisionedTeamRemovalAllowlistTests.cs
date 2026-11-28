using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <see cref="TeamRepository.LockProvisionedRemovalAsync"/> removes whatever team it is given, asking no manager, so it
/// is limited to the members listed here, each with the reason the team it removes is one the instance keeps itself. An
/// entry names the file and the member the call sits in, so a new member in an allowlisted file is not covered by its
/// neighbour. A new caller is added here deliberately or not at all.
/// </summary>
[Trait("Category", "Unit")]
public class ProvisionedTeamRemovalAllowlistTests
{
    private const string Method = nameof(TeamRepository.LockProvisionedRemovalAsync);

    private static readonly string[] ScannedTrees = ["src"];

    private static readonly string[] Allowed =
    [
        // The world reset removes the demo clinic's team, read from the demo host's own config, under the clinic lock.
        "src/API/Nocturne.API/Services/Demo/DemoTenantService.cs: DemoTenantService.RemoveClinicTeamAsync",
    ];

    private static readonly IReadOnlyList<string> Callers = Scan();

    [Fact]
    public void OnlyTheAllowlistRemovesAProvisionedTeam()
    {
        Callers.Except(Allowed).Should().BeEmpty(
            $"{Method} removes any team with no manager's say; allowlist a new caller here deliberately");
    }

    [Fact]
    public void EveryAllowlistEntryStillCallsIt()
    {
        Allowed.Except(Callers).Should().BeEmpty("a stale entry would silently cover a future call in that member");
    }

    [Theory]
    [InlineData("class C { Task M(DbUnitOfWork u) => TeamRepository.LockProvisionedRemovalAsync(u, id, ct); }", "C.M")]
    [InlineData("class C { Task M() => Run(async (u, t) => await TeamRepository.LockProvisionedRemovalAsync(u, id, t)); }", "C.M")]
    [InlineData("class C { void M() => Use(LockProvisionedRemovalAsync); }", "C.M")]
    [InlineData("class C { Task M(DbUnitOfWork u) => TeamRepository.LockTeamDeletionAsync(u, id, caller, ct); }", null)]
    public void FindsTheCallingMember(string source, string? expected)
    {
        var members = CallingMembers(CSharpSyntaxTree.ParseText(source));

        if (expected is null)
            members.Should().BeEmpty();
        else
            members.Should().Equal(expected);
    }

    /// <summary>The member around every mention of <see cref="Method"/> by name, a lambda counting as the member it is written in.</summary>
    private static List<string> CallingMembers(SyntaxTree syntax) =>
        syntax.GetRoot()
            .DescendantNodes()
            .Where(node => node switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == Method,
                IdentifierNameSyntax name => name.Identifier.ValueText == Method && name.Parent is not MemberAccessExpressionSyntax,
                _ => false,
            })
            .Select(EnclosingMember.Of)
            .Distinct()
            .ToList();

    private static List<string> Scan() =>
        ScannedTrees.SelectMany(RepoSources.Under)
            .Where(source => source.Text.Contains(Method, StringComparison.Ordinal))
            .SelectMany(source => CallingMembers(source.Tree).Select(member => $"{source.Path}: {member}"))
            .ToList();
}
