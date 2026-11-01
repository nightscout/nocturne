using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <see cref="INocturneDbContextFactory.CreateSystemPinnedAsync"/> reads as no reader, past every
/// share and history narrowing, so it is limited to the members listed here; its remarks give the
/// reason. The <see cref="ISubjectRepository"/> and <see cref="ITenantMembershipRepository"/> reads
/// that take the pin inside Infrastructure are limited the same way in the host, so a new caller
/// of any of them gets a cross-tenant, share-blind read only by adding itself here. An entry names the file and the member the call
/// sits in, so a new action in an allowlisted file is not covered by its neighbour. A new caller
/// is added here deliberately or not at all.
/// </summary>
[Trait("Category", "Unit")]
public class SystemPinnedAllowlistTests
{
    private const string Method = nameof(INocturneDbContextFactory.CreateSystemPinnedAsync);

    /// <summary>The repository reads that pin the system inside Infrastructure.</summary>
    private static readonly string[] SystemPinnedReads =
    [
        nameof(ISubjectRepository.TenantHasCredentialedMemberAsync),
        nameof(ISubjectRepository.HasOrphanedMemberAsync),
        nameof(ITenantMembershipRepository.ReadSetupGateAsync),
        nameof(ITenantMembershipRepository.CountSetupCredentialsAsync),
        nameof(ITenantMembershipRepository.ListOrphanedMembersAsync),
        nameof(ITenantMembershipRepository.GetOwnerSubjectIdsForAccessRequestAsync),
    ];

    private static readonly string[] ScannedTrees = ["src"];

    private static readonly string[] Allowed =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.TenantHasCredentialedMemberAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.HasOrphanedMemberAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TenantMembershipRepository.cs: TenantMembershipRepository.GetOwnerSubjectIdsForAccessRequestAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TenantMembershipRepository.cs: TenantMembershipRepository.ReadSetupGateAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TenantMembershipRepository.cs: TenantMembershipRepository.CountSetupCredentialsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TenantMembershipRepository.cs: TenantMembershipRepository.ListOrphanedMembersAsync",
    ];

    /// <summary>
    /// The members outside the module that call a <see cref="SystemPinnedReads"/> read: the anonymous auth
    /// status a share host's login page asks, the setup state asked on public-share requests, the
    /// setup gate every API request passes through, and the owners told of an access request made
    /// from a share host's login page. The scan is by name, so
    /// <c>IInstanceSetupState.TenantHasCredentialedMemberAsync</c>, the facade over the same pinned
    /// read, pins its callers too.
    /// </summary>
    private static readonly string[] AllowedReadCallers =
    [
        "src/API/Nocturne.API/Controllers/Authentication/PasskeyController.cs: PasskeyController.AccessRequestComplete",
        "src/API/Nocturne.API/Controllers/Authentication/PasskeyController.cs: PasskeyController.GetAuthStatus",
        "src/API/Nocturne.API/Controllers/V4/SetupController.cs: SetupController.GetSoleTenantWithoutOwnerAsync",
        "src/API/Nocturne.API/Services/Identity/InstanceSetupState.cs: InstanceSetupState.IsSetupCompleteAsync",
        "src/API/Nocturne.API/Services/Identity/InstanceSetupState.cs: InstanceSetupState.TenantHasCredentialedMemberAsync",
        "src/API/Nocturne.API/Middleware/TenantSetupMiddleware.cs: TenantSetupMiddleware.EvaluateGateAsync",
    ];

    private static readonly IReadOnlyList<string> Callers = Scan(ScannedTrees, [Method]);

    /// <summary>Where the pinned reads live; every other tree is a caller.</summary>
    private const string Module = "src/Infrastructure/Nocturne.Infrastructure.Data/";

    private static readonly IReadOnlyList<string> ReadCallers = Scan(ScannedTrees, SystemPinnedReads)
        .Where(caller => !caller.StartsWith(Module, StringComparison.Ordinal))
        .ToList();

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

    [Fact]
    public void OnlyTheAllowlistedHostMembersCallTheSystemPinnedReads()
    {
        ReadCallers.Except(AllowedReadCallers).Should().BeEmpty(
            "the repository's system-pinned reads bypass share narrowing; allowlist a new host caller here deliberately");
    }

    [Fact]
    public void EveryAllowlistedHostMemberStillCallsAPinnedRead()
    {
        AllowedReadCallers.Except(ReadCallers).Should().BeEmpty("a stale entry would silently cover a future call in that member");
    }

    [Fact]
    public void AnUnlistedCallerOfAPinnedReadIsReported()
    {
        var unlisted = new SourceFile(
            "src/API/Nocturne.API/Controllers/V4/Elsewhere.cs",
            "class Elsewhere { async Task<bool> M(ISubjectRepository s, Guid t) => await s.TenantHasCredentialedMemberAsync(t, default); }");

        var callers = CallingMembers(unlisted.Tree, SystemPinnedReads).Select(member => $"{unlisted.Path}: {member}");

        callers.Except(AllowedReadCallers).Should().Equal("src/API/Nocturne.API/Controllers/V4/Elsewhere.cs: Elsewhere.M");
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
        var members = CallingMembers(CSharpSyntaxTree.ParseText(source), [Method]);

        if (expected is null)
            members.Should().BeEmpty();
        else
            members.Should().Equal(expected);
    }

    [Theory]
    [InlineData("class C { Task<bool> M(ISubjectRepository s) => s.HasOrphanedMemberAsync(t, ct); }", "C.M")]
    [InlineData("class C { Task<bool> M(ISubjectRepository? s) => s?.TenantHasCredentialedMemberAsync(t, ct); }", "C.M")]
    [InlineData("class C { void M(ISubjectRepository s) => Use(s.TenantHasCredentialedMemberAsync); }", "C.M")]
    [InlineData("class C { Task<bool> M(ISubjectRepository s) => s.IsPlatformAdminAsync(id, ct); }", null)]
    public void FindsTheMemberCallingAPinnedRead(string source, string? expected)
    {
        var members = CallingMembers(CSharpSyntaxTree.ParseText(source), SystemPinnedReads);

        if (expected is null)
            members.Should().BeEmpty();
        else
            members.Should().Equal(expected);
    }

    /// <summary>
    /// The member around every mention of one of <paramref name="methods"/> by name: a call, a
    /// conditional call (<c>f?.Method(...)</c>) or a method group passed on. A local function or
    /// lambda counts as the member it is written in.
    /// </summary>
    private static List<string> CallingMembers(SyntaxTree syntax, IReadOnlyCollection<string> methods) =>
        syntax.GetRoot()
            .DescendantNodes()
            .Where(node => node switch
            {
                MemberAccessExpressionSyntax member => methods.Contains(member.Name.Identifier.ValueText),
                MemberBindingExpressionSyntax binding => methods.Contains(binding.Name.Identifier.ValueText),
                IdentifierNameSyntax name => methods.Contains(name.Identifier.ValueText)
                    && name.Parent is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax),
                _ => false,
            })
            .Select(EnclosingMember.Of)
            .Distinct()
            .ToList();

    private static List<string> Scan(IEnumerable<string> trees, IReadOnlyCollection<string> methods)
    {
        var callers = new List<string>();

        foreach (var source in trees.SelectMany(RepoSources.Under))
        {
            if (!methods.Any(method => source.Text.Contains(method, StringComparison.Ordinal)))
                continue;

            callers.AddRange(CallingMembers(source.Tree, methods)
                .Select(member => $"{source.Path}: {member}"));
        }

        return callers;
    }
}
