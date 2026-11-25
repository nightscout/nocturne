using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A membership whose reason has lapsed is due, and only a read that recomputes it may select it by
/// its end alone (<c>MembershipLiveness.IsUnended</c>), through <c>MembershipReasons.ReadAllInForceAsync</c>.
/// Any other read that selected by end alone would hand out the access the lapsed reason gave, so an
/// end tested by hand (<c>HasEnded</c> on an <c>AccessEndsAt</c>, or an inline null-or-later test)
/// outside <c>MembershipLiveness</c> is listed with why it may.
/// </summary>
[Trait("Category", "Unit")]
public class EndOnlyMembershipReadGuardTests
{
    private const string Repositories = "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories";
    private const string MembershipReasons = "src/Infrastructure/Nocturne.Infrastructure.Data/Services/MembershipReasons.cs";
    private const string MembershipLiveness = "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/MembershipLiveness.cs";

    /// <summary>The reads that recompute a due row they find, each with why it may not wait for the sweep.</summary>
    private static readonly Dictionary<string, string> Readers = new()
    {
        [$"{Repositories}/TenantMembershipRepository.cs: TenantMembershipRepository.ReadSubjectMembershipAsync"] =
            "the membership every authenticated request reads",
        [$"{Repositories}/OAuthGrantRepository.cs: OAuthGrantRepository.FindLiveDirectGrantAsync"] =
            "the membership a request with a direct grant reads beside the grant",
        [$"{Repositories}/TenantMembershipRepository.cs: TenantMembershipRepository.GetMemberAccessAsync"] =
            "the access the hubs re-prove every minute and alert acknowledgements check",
        [$"{Repositories}/TenantMembershipRepository.cs: TenantMembershipRepository.IsMemberAsync"] =
            "the membership a sign-in admits by",
        [$"{Repositories}/TenantMembershipRepository.cs: TenantMembershipRepository.GetMemberRoleNamesAsync"] =
            "the roles an OIDC sign-in puts in its claims",
        [$"{Repositories}/SubjectRepository.cs: SubjectRepository.FindMemberByUsernameAsync"] =
            "the membership a passkey sign-in by username admits by",
        [$"{Repositories}/SubjectRepository.cs: SubjectRepository.FindMembersByConfirmedEmailAsync"] =
            "the memberships an email-code sign-in and account linking admit by",
    };

    private const string ReadAllInForce = $"{MembershipReasons}: MembershipReasons.ReadAllInForceAsync";

    /// <summary>The ends tested by hand outside <c>MembershipLiveness</c>, each with why it may test the end alone.</summary>
    private static readonly Dictionary<string, string> EndTests = new()
    {
        ["src/API/Nocturne.API/Controllers/V4/Identity/RoleController.cs: RoleController.GetRoles"] =
            "the caller's own membership, which the request's membership read has already recomputed",
        ["src/API/Nocturne.API/Services/Identity/MembershipRemoval.cs: MembershipRemoval.ReplaceEndedAsync"] =
            "whether a row has ended and may be replaced; a due row that has not ended is still a member",
        ["src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.GetByIdAsync"] =
            "the ended flag the member list shows beside the end it shows",
        ["src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.ReasonsOf"] =
            "the ended flag the member list shows beside each reason's own end",
        ["src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.DirectLiftsTeamLimit"] =
            "which reasons the member list's note on a lifted limit names, not whether the row is a member",
        [$"{Repositories}/TenantMembershipRepository.cs: TenantMembershipRepository.IsDirectMemberAsync"] =
            "whether the direct reason itself is in force, so a person is not let in directly twice; access checks keep IsMemberAsync",
        [$"{Repositories}/TenantMembershipRepository.cs: TenantMembershipRepository.StageSetTeamReasonAsync"] =
            "whether one team reason's own end had passed and its new grant's has not, which the history states as access again, not whether the row is a member",
        [$"{Repositories}/MembershipReasonColumns.cs: MembershipReasonColumns.DirectAccess"] =
            "the direct reason's own end, which decides what the member may give, not whether the row is a member",
        [$"{Repositories}/TeamRepository.Access.cs: TeamRepository.ListAccessOfTenantAsync"] =
            "a team's access to a site, not a membership",
        [$"{Repositories}/TeamRepository.Access.cs: TeamRepository.SitesOfAsync"] =
            "a team's access to a site, not a membership",
        [$"{Repositories}/TeamRepository.Access.cs: TeamRepository.WithStaffWaitingLockedAsync"] =
            "a team's access to a site and a staff member's place in the team, which decide who an owner is told waits, not a membership",
        [$"{Repositories}/TeamReasonChanges.cs: TeamReasonChanges.SyncAsync"] =
            "whether one team reason has lapsed, which is left in place, or would be added already ended, not whether the row is a member",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Services/TeamReasons.cs: TeamReasons.OwnsAsync"] =
            "whether the direct reason, or the row that stands for it, still gives the owner role, not whether the row is a member",
        ["src/Core/Nocturne.Core.Models/Identity/MembershipSum.cs: MembershipSum.InForceAt"] =
            "a reason's own end: the rule that decides which reasons are in force",
        ["src/Infrastructure/Nocturne.Infrastructure.Data/Services/TeamAccessRules.cs: TeamAccessRules.Refusal"] =
            "a grant's chosen end against the latest end its link allows, not liveness",
    };

    [Fact]
    public void OnlyTheRequestAndSignInReadsRecomputeOnRead() =>
        CallersOf("ReadInForceAsync", "ReadAllInForceAsync")
            .Where(caller => !caller.StartsWith(MembershipReasons))
            .Should().BeEquivalentTo(Readers.Keys);

    [Fact]
    public void OnlyTheReadThatRecomputesSelectsByEndAlone() =>
        CallersOf("IsUnended").Should().Equal(ReadAllInForce);

    [Fact]
    public void EveryEndTestedByHandIsListed() =>
        RepoSources.Src
            .Where(file => file.Path != MembershipLiveness)
            .SelectMany(file => EndTestsIn(CSharpSyntaxTree.ParseText(file.Text))
                .Select(node => $"{file.Path}: {EnclosingMember.Of(node)}"))
            .Distinct()
            .Should().BeEquivalentTo(EndTests.Keys);

    [Theory]
    [InlineData("class C { void M() { MembershipReasons.ReadInForceAsync(c, t, n, r, ct); } }", 1)]
    [InlineData("class C { void M() { MembershipReasons.ReadAllInForceAsync(c, t, n, r, ct); } }", 1)]
    [InlineData("class C { void M() { q.Where(MembershipLiveness.IsUnended(now)); } }", 1)]
    [InlineData("class C { void M() { q.Where(IsUnended(now)); } }", 1)]
    [InlineData("class C { /// <see cref=\"MembershipLiveness.IsUnended\"/>\n void M() { } }", 0)]
    [InlineData("class C { void M() { q.Where(MembershipLiveness.IsLive(now)); } }", 0)]
    public void TheGuardSeesACall(string source, int expected) =>
        Calls(CSharpSyntaxTree.ParseText(source), ["ReadInForceAsync", "ReadAllInForceAsync", "IsUnended"]).Should().HaveCount(expected);

    [Theory]
    [InlineData("class C { bool M() => !MembershipLiveness.HasEnded(member.AccessEndsAt, now); }", 1)]
    [InlineData("class C { void M() { q.Where(m => m.AccessEndsAt == null || m.AccessEndsAt > now); } }", 1)]
    [InlineData("class C { void M() { q.Where(m => m.AccessEndsAt is null || m.AccessEndsAt > now); } }", 1)]
    [InlineData("class C { void M() { q.Where(m => m.AccessEndsAt is not { } end || end > now); } }", 1)]
    [InlineData("class C { void M() { q.Where(m => m.AccessEndsAt <= now); } }", 1)]
    [InlineData("class C { void M() { q.Where(MembershipLiveness.LiveAt(now)); } }", 0)]
    [InlineData("class C { bool M() => MembershipLiveness.HasEnded(end, now); }", 0)]
    [InlineData("class C { bool M() => member.AccessEndsAt != accessEndsAt; }", 0)]
    public void TheGuardSeesAnEndTestedByHand(string source, int expected) =>
        EndTestsIn(CSharpSyntaxTree.ParseText(source)).Should().HaveCount(expected);

    private static List<string> CallersOf(params string[] methods) =>
        RepoSources.Src
            .SelectMany(file => Calls(CSharpSyntaxTree.ParseText(file.Text), methods)
                .Select(call => $"{file.Path}: {EnclosingMember.Of(call)}"))
            .Distinct()
            .ToList();

    private static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxTree tree, string[] methods) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => methods.Contains(NameOf(call.Expression)));

    private static string? NameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    private static IEnumerable<SyntaxNode> EndTestsIn(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().Where(node => node switch
        {
            InvocationExpressionSyntax call => NameOf(call.Expression) == "HasEnded"
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } first && IsAccessEnd(first),
            BinaryExpressionSyntax either when either.IsKind(SyntaxKind.LogicalOrExpression) => IsNullOrLaterTest(either),
            BinaryExpressionSyntax relational when relational.IsKind(SyntaxKind.LessThanExpression)
                || relational.IsKind(SyntaxKind.LessThanOrEqualExpression)
                || relational.IsKind(SyntaxKind.GreaterThanExpression)
                || relational.IsKind(SyntaxKind.GreaterThanOrEqualExpression) =>
                (IsAccessEnd(relational.Left) || IsAccessEnd(relational.Right))
                && !relational.Ancestors().OfType<BinaryExpressionSyntax>().Any(IsNullOrLaterTest),
            _ => false,
        });

    private static bool IsNullOrLaterTest(BinaryExpressionSyntax either) =>
        either.IsKind(SyntaxKind.LogicalOrExpression) && (IsNullTestOfAccessEnd(either.Left) || IsNullTestOfAccessEnd(either.Right));

    private static bool IsAccessEnd(ExpressionSyntax expression) =>
        expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AccessEndsAt" };

    private static bool IsNullTestOfAccessEnd(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsNullTestOfAccessEnd(parenthesized.Expression),
        BinaryExpressionSyntax equals when equals.IsKind(SyntaxKind.EqualsExpression) =>
            (IsAccessEnd(equals.Left) && equals.Right.IsKind(SyntaxKind.NullLiteralExpression))
            || (IsAccessEnd(equals.Right) && equals.Left.IsKind(SyntaxKind.NullLiteralExpression)),
        IsPatternExpressionSyntax pattern => IsAccessEnd(pattern.Expression),
        _ => false,
    };
}
