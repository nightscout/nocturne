using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>AddMemberAsync</c> refuses a subject who holds a membership for any reason, so a path that lets a
/// person in directly (an invite, a request, a manager) and called it would refuse a team's staff
/// member. Those paths call <c>AddDirectMemberAsync</c>; the callers left are listed here with their
/// reason, and a new one is added deliberately or not at all.
/// </summary>
/// <remarks>
/// The scan matches the name, so it also lists the callers of <c>ITenantService.AddMemberAsync</c>,
/// which is a manager's direct add and goes through <c>AddDirectMemberAsync</c> itself.
/// </remarks>
[Trait("Category", "Unit")]
public class AddMemberCallerAllowlistTests
{
    private const string Method = "AddMemberAsync";

    private static readonly string[] Allowed =
    [
        // ITenantService.AddMemberAsync, a manager's direct add.
        "src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs: DevAdminController.EnsureMemberAsync",
        "src/API/Nocturne.API/Controllers/V4/PlatformAdmin/TenantController.cs: TenantController.AddMember",
        // Setup makes the first owner of a tenant nobody else can reach yet.
        "src/API/Nocturne.API/Controllers/V4/SetupController.cs: SetupController.EnsureOwnerSubjectAsync",
        // The demo member is a system member of the demo tenant.
        "src/API/Nocturne.API/Services/Demo/DemoTenantService.cs: DemoTenantService.EnsureDemoMemberAsync",
        // A new tenant's owner, which no team can hold a reason on before the tenant exists.
        "src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.CreateAsync",
        "src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.CreateWithOwnerAsync",
        "src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.ProvisionWithOwnerAsync",
        // The Public system member.
        "src/API/Nocturne.API/Services/Identity/TenantService.cs: TenantService.CreatePublicSubjectMembershipAsync",
    ];

    private static readonly IReadOnlyList<string> Callers = Scan();

    [Fact]
    public void OnlyTheAllowlistAddsAMemberWithoutAskingForADirectReason()
    {
        Callers.Except(Allowed).Should().BeEmpty(
            $"{Method} refuses a team's staff member; a path that lets a person in directly calls AddDirectMemberAsync");
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
