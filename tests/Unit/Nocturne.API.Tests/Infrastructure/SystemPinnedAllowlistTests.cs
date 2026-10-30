using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <see cref="INocturneDbContextFactory.CreateSystemPinnedAsync"/> reads as no reader, past every
/// share and history narrowing, so it is limited to the members listed here; its remarks give the
/// reason. An entry names the file and the member the call sits in, so a new action in an
/// allowlisted file is not covered by its neighbour. A new caller is added here deliberately or not
/// at all.
/// </summary>
[Trait("Category", "Unit")]
public class SystemPinnedAllowlistTests
{
    private const string Method = nameof(INocturneDbContextFactory.CreateSystemPinnedAsync);

    private static readonly string[] ScannedTrees = ["src"];

    private static readonly string[] Allowed =
    [
        "src/API/Nocturne.API/Middleware/TenantSetupMiddleware.cs: TenantSetupMiddleware.EvaluateGateAsync",
        "src/API/Nocturne.API/Controllers/Authentication/PasskeyController.cs: PasskeyController.AccessRequestComplete",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.TenantHasCredentialedMemberAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.HasOrphanedMemberAsync",
    ];

    private static readonly IReadOnlyList<string> Callers = Scan();

    [Fact]
    public void OnlyTheAllowlistCallsTheSystemPin()
    {
        Callers.Except(Allowed).Should().BeEmpty(
            $"{Method} bypasses share and history narrowing; allowlist a new caller here deliberately");
    }

    [Fact]
    public void EveryAllowlistEntryStillCallsIt()
    {
        Allowed.Except(Callers).Should().BeEmpty("a stale entry would silently cover a future call in that member");
    }

    [Theory]
    [InlineData("class C { async Task M(INocturneDbContextFactory f) => await f.CreateSystemPinnedAsync(id); }", "C.M")]
    [InlineData("class C { async Task M(INocturneDbContextFactory? f) => await f?.CreateSystemPinnedAsync(id); }", "C.M")]
    [InlineData("class C { Func<Guid, CancellationToken, ValueTask<NocturneDbContext>> M(INocturneDbContextFactory f) => f.CreateSystemPinnedAsync; }", "C.M")]
    [InlineData("class C { void M() => Use(CreateSystemPinnedAsync); }", "C.M")]
    [InlineData("class C { void M() { async Task Local() => await f.CreateSystemPinnedAsync(id); } }", "C.M")]
    [InlineData("class C { C(INocturneDbContextFactory f) => f.CreateSystemPinnedAsync(id); }", "C.C")]
    [InlineData("class C { Task P => f.CreateSystemPinnedAsync(id).AsTask(); }", "C.P")]
    [InlineData("class C { Func<Task> F = () => f.CreateSystemPinnedAsync(id).AsTask(); }", "C.F")]
    [InlineData("class O { class C { void M() => f.CreateSystemPinnedAsync(id); } }", "O.C.M")]
    [InlineData("class C { async Task M(INocturneDbContextFactory f) => await f.CreateForTenantAsync(id); }", null)]
    public void FindsTheCallingMember(string source, string? expected)
    {
        var members = CallingMembers(CSharpSyntaxTree.ParseText(source));

        if (expected is null)
            members.Should().BeEmpty();
        else
            members.Should().Equal(expected);
    }

    /// <summary>
    /// The member around every mention of the method by name: a call, a conditional call
    /// (<c>f?.Method(...)</c>) or a method group passed on. A local function or lambda counts as
    /// the member it is written in.
    /// </summary>
    private static List<string> CallingMembers(SyntaxTree syntax) =>
        syntax.GetRoot()
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
            .ToList();

    private static List<string> Scan()
    {
        var callers = new List<string>();

        foreach (var source in ScannedTrees.SelectMany(RepoSources.Under))
        {
            if (!source.Text.Contains(Method, StringComparison.Ordinal))
                continue;

            callers.AddRange(CallingMembers(source.Tree)
                .Select(member => $"{source.Path}: {member}"));
        }

        return callers;
    }
}
