using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>GetGrantedScopes</c>, <c>IsCallerHistoryClamped</c>, <c>CallerAccessEndsAt</c> and
/// <c>ICategoryReadContext.IsHistoryClamped</c> read the sum of a member's reasons, a team's included.
/// What a member gives anyone else is bounded by their direct reason alone
/// (<c>HttpContextExtensions.CallerGrantCeiling</c>), so every file that reads the sum is pinned here
/// with why it may, and the sites that give to others may read none of them, nor the sum through
/// <c>HasScope</c>, <c>HasPermission</c>, <c>IsSuperuser</c>, the credential's own
/// <c>AuthContext.Scopes</c> or <c>RequestMembership.Of</c>.
/// </summary>
[Trait("Category", "Unit")]
public class GrantCeilingSumReaderGuardTests
{
    private static readonly string[] SumReaders = ["GetGrantedScopes", "IsCallerHistoryClamped", "CallerAccessEndsAt"];

    /// <summary>Properties that read the sum; a method of the same name (<c>MemberScopeResolver.IsHistoryClamped</c>) only computes.</summary>
    private static readonly string[] SumProperties = ["IsHistoryClamped"];

    /// <summary>Calls that read the sum through another name.</summary>
    private static readonly string[] SumAliases = ["HasScope", "HasPermission", "IsSuperuser"];

    private enum Reads
    {
        /// <summary>Decides what the caller may read or do on the site, which the sum governs.</summary>
        AccessCheck,

        /// <summary>Bounds a credential the caller holds, which every request intersects with the live membership.</summary>
        OwnCredential,

        /// <summary>Stamps what the caller makes with the end of their access, so it stops when they lose it.</summary>
        LivesWithSum,

        /// <summary>An operator acting on the instance, not a member giving from their own access.</summary>
        Operator,

        /// <summary>Hands the credential's scopes on for a check made against another site's direct reason.</summary>
        ApproverCredential,

        /// <summary>Defines the readers, or builds the ceiling from them.</summary>
        Accessor,

        /// <summary>
        /// Reads the sum for a caller who must hold <c>*</c>, which only a direct reason can hold (a team
        /// reason is data only), and who is exempt from the 24-hour limit, so the sum and the ceiling agree.
        /// </summary>
        HoldsFullAccess,
    }

    private static readonly Dictionary<string, (Reads Class, string Reason)> Pinned = new()
    {
        ["src/API/Nocturne.API/Attributes/RequireDeclaredWriteScopeAttribute.cs"] = (Reads.AccessCheck, "gates a write on the caller's scopes"),
        ["src/API/Nocturne.API/Attributes/RequireScopeAttribute.cs"] = (Reads.AccessCheck, "gates an action on the caller's scopes"),
        ["src/API/Nocturne.API/Authorization/ActivityReadScopeGuard.cs"] = (Reads.AccessCheck, "redacts activity the caller may not read"),
        ["src/API/Nocturne.API/Authorization/LegacyStorageReadScopes.cs"] = (Reads.AccessCheck, "gates a legacy collection read"),
        ["src/API/Nocturne.API/Controllers/Authentication/DirectGrantController.cs"] = (Reads.OwnCredential, "Create bounds the caller's own token; Keep reads the ceiling"),
        ["src/API/Nocturne.API/Controllers/Authentication/OAuthController.cs"] = (Reads.OwnCredential, "consent, device approval and an app grant's edit bound the caller's own app; a guest grant's edit is bounded by the ceiling in OAuthGrantService"),
        ["src/API/Nocturne.API/Controllers/V1/ActivityController.cs"] = (Reads.AccessCheck, "redacts activity the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V1/AuthenticationController.cs"] = (Reads.AccessCheck, "reports what the caller's credential may do"),
        ["src/API/Nocturne.API/Controllers/V2/DDataController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/ActogramController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/ChartDataController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/CorrelationController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/CurrentTherapyStateController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/CustomReportsController.cs"] = (Reads.AccessCheck, "limits the caller's reads and report edits"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/LoopAnalysisController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/PredictionController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/RetrospectiveController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/StateSpansController.cs"] = (Reads.AccessCheck, "gates span reads and writes by category"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/StatisticsController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/SummaryController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Analytics/TreatmentLogController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/ClientDevices/ClientDevicesController.cs"] = (Reads.OwnCredential, "registers the caller's own device"),
        ["src/API/Nocturne.API/Controllers/V4/Connectors/CareLinkConnectController.cs"] = (Reads.AccessCheck, "gates the sign-in on tenant.settings"),
        ["src/API/Nocturne.API/Controllers/V4/Health/ActivityController.cs"] = (Reads.AccessCheck, "redacts activity the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Health/Oura/OuraController.cs"] = (Reads.AccessCheck, "refuses an import the clamped caller could not read back"),
        ["src/API/Nocturne.API/Controllers/V4/Identity/MyPermissionsController.cs"] = (Reads.AccessCheck, "reports the caller's own scopes and 24-hour limit"),
        ["src/API/Nocturne.API/Controllers/V4/Identity/MyTenantsController.cs"] = (Reads.AccessCheck, "resolves the caller's own access per tenant"),
        ["src/API/Nocturne.API/Controllers/V4/Identity/OverviewAdmissionController.cs"] = (Reads.AccessCheck, "lists the tenants the caller may read"),
        ["src/API/Nocturne.API/Controllers/V4/Identity/SetupAlertsController.cs"] = (Reads.LivesWithSum, "stamps the caller's own alert channels with their end"),
        ["src/API/Nocturne.API/Controllers/V4/Identity/TeamsController.cs"] = (Reads.ApproverCredential, "TeamRepository judges the approval by the chosen site's direct reason"),
        ["src/API/Nocturne.API/Controllers/V4/Monitoring/AlertReplayController.cs"] = (Reads.AccessCheck, "redacts and clamps the caller's replay"),
        ["src/API/Nocturne.API/Controllers/V4/Monitoring/AlertRulesController.cs"] = (Reads.LivesWithSum, "stamps the caller's own alert channels with their end"),
        ["src/API/Nocturne.API/Controllers/V4/Platform/ServicesController.cs"] = (Reads.LivesWithSum, "a saved secret stops with the credential maker's end; who may adopt one reads the ceiling"),
        ["src/API/Nocturne.API/Controllers/V4/PlatformAdmin/AccessRequestController.cs"] = (Reads.Operator, "a platform admin approves on the instance's behalf"),
        ["src/API/Nocturne.API/Controllers/V4/Profiles/ProfileHistoryController.cs"] = (Reads.AccessCheck, "redacts what the caller may not read"),
        ["src/API/Nocturne.API/Controllers/V4/Profiles/UISettingsController.cs"] = (Reads.AccessCheck, "gates a settings section on its read scope"),
        ["src/API/Nocturne.API/Controllers/V4/TenantAdmin/RejectedUploadsController.cs"] = (Reads.AccessCheck, "refuses a clamped caller a history read"),
        ["src/API/Nocturne.API/Extensions/HttpContextExtensions.cs"] = (Reads.Accessor, "defines the readers and the ceiling's fallback"),
        ["src/API/Nocturne.API/Hubs/HubAuthorization.cs"] = (Reads.AccessCheck, "carries the connection's scopes to its hub checks"),
        ["src/API/Nocturne.API/Hubs/OverviewHub.cs"] = (Reads.AccessCheck, "admits the caller's own overview"),
        ["src/API/Nocturne.API/Middleware/MemberScopeMiddleware.cs"] = (Reads.Accessor, "resolves the sum and the ceiling from the same credential"),
        ["src/API/Nocturne.API/Reports/ReportCatalogue.cs"] = (Reads.AccessCheck, "offers the reports the caller may read"),
        ["src/API/Nocturne.API/Reports/ReportRenderer.cs"] = (Reads.AccessCheck, "redacts and clamps the caller's report"),
        ["src/API/Nocturne.API/Services/Identity/AuthorizationService.cs"] = (Reads.HoldsFullAccess, "limits a device subject's token as its [RequireSuperuser] maker reads"),
        ["src/API/Nocturne.API/Services/Platform/StatusService.cs"] = (Reads.AccessCheck, "reports only what the caller may read"),
    };

    /// <summary>
    /// The sites that give someone else access, whole files or <c>file: Type.Member</c>. Each reads
    /// <c>CallerGrantCeiling</c> and none of the sum readers.
    /// </summary>
    private static readonly string[] GivesToOthers =
    [
        "src/API/Nocturne.API/Controllers/V4/Identity/GuestLinkController.cs",
        "src/API/Nocturne.API/Controllers/V4/Identity/MemberInviteController.cs",
        "src/API/Nocturne.API/Controllers/V4/Identity/MemberRecoveryController.cs",
        "src/API/Nocturne.API/Controllers/V4/Identity/MembershipRequestController.cs",
        "src/API/Nocturne.API/Controllers/V4/Identity/RoleController.cs",
        "src/API/Nocturne.API/Controllers/V4/Identity/ShareLinkController.cs",
        "src/API/Nocturne.API/Controllers/Authentication/DirectGrantController.cs: DirectGrantController.Keep",
        "src/API/Nocturne.API/Controllers/Authentication/OAuthController.cs: OAuthController.UpdateGrant",
        "src/API/Nocturne.API/Controllers/V4/Connectors/ConfigurationController.cs: ConfigurationController.SaveSecrets",
        "src/API/Nocturne.API/Controllers/V4/Platform/ServicesController.cs: ServicesController.GetConnections",
        $"{Extensions}: HttpContextExtensions.OutlastsCallerAccess",
        $"{Extensions}: HttpContextExtensions.MemberOutlastsCallerRefusal",
        $"{Extensions}: HttpContextExtensions.InheritAccessEnd",
        $"{Extensions}: HttpContextExtensions.InheritGivenHistoryClamp",
        $"{Extensions}: HttpContextExtensions.PublicSharingEndDateRefusal",
        $"{Extensions}: HttpContextExtensions.MayKeepDeviceCredentials",
    ];

    private const string Extensions = "src/API/Nocturne.API/Extensions/HttpContextExtensions.cs";

    /// <summary>
    /// The members of a giving site that read the sum through an alias to decide what the caller may
    /// do or see, not what they give, each with why.
    /// </summary>
    private static readonly Dictionary<string, string> AccessChecksInGivingSites = new()
    {
        ["src/API/Nocturne.API/Controllers/V4/Identity/MemberInviteController.cs: MemberInviteController.GetMembers"] =
            "whether the member list shows a manager's detail; what may be given reads the ceiling",
        ["src/API/Nocturne.API/Controllers/V4/Identity/RoleController.cs: RoleController.GetRoles"] =
            "whether the list shows every role to a role manager or the assignable ones; CanGrant reads the ceiling",
    };

    private static readonly IReadOnlyList<(string File, string Member)> Calls = Scan();

    private static readonly IReadOnlyList<(string File, string Member)> AliasCalls = ScanAliases();

    [Fact]
    public void EveryFileThatReadsTheSumIsPinned()
    {
        Calls.Select(c => c.File).Distinct().Except(Pinned.Keys).Should().BeEmpty(
            "a file that reads the sum is pinned here with its class and why; one that gives to others reads CallerGrantCeiling");
    }

    [Fact]
    public void EveryPinnedFileStillReadsTheSum()
    {
        Pinned.Keys.Except(Calls.Select(c => c.File)).Should().BeEmpty("a stale entry would silently cover a future call");
    }

    [Fact]
    public void NoSiteThatGivesToOthersReadsTheSum()
    {
        Calls.Where(c => GivesToOthers.Any(site => Covers(site, c)))
            .Select(c => $"{c.File}: {c.Member}")
            .Should().BeEmpty("what a member gives anyone else is bounded by CallerGrantCeiling, not the sum");
    }

    [Fact]
    public void NoSiteThatGivesToOthersReadsTheSumThroughAnAlias()
    {
        AliasCalls.Where(c => GivesToOthers.Any(site => Covers(site, c)))
            .Select(c => $"{c.File}: {c.Member}")
            .Except(AccessChecksInGivingSites.Keys)
            .Should().BeEmpty("HasScope, HasPermission, IsSuperuser, AuthContext.Scopes and RequestMembership.Of read the sum too");
    }

    [Fact]
    public void EveryAccessCheckInAGivingSiteStillReadsAnAlias()
    {
        AccessChecksInGivingSites.Keys.Except(AliasCalls.Select(c => $"{c.File}: {c.Member}"))
            .Should().BeEmpty("a stale entry would silently cover a future call");
    }

    [Fact]
    public void EverySiteThatGivesToOthersStillExists()
    {
        foreach (var site in GivesToOthers)
        {
            var (file, member) = Split(site);
            var source = RepoSources.Under("src/API").SingleOrDefault(s => s.Path == file);
            source.Should().NotBeNull($"{file} is listed as giving to others");
            if (member is null)
                continue;

            source!.Tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(method => EnclosingMember.Of(method.ParameterList)).Should().Contain(member);
        }
    }

    [Fact]
    public void NoFileIsPinnedAndListedAsAWholeGivingSite()
    {
        Pinned.Keys.Intersect(GivesToOthers).Should().BeEmpty();
    }

    private static bool Covers(string site, (string File, string Member) call) =>
        Split(site) is var (file, member) && file == call.File && (member is null || member == call.Member);

    private static (string File, string? Member) Split(string site) =>
        site.Split(": ", 2) is [var file, var member] ? (file, member) : (site, null);

    private static List<(string File, string Member)> Scan() =>
        Find(SumReaders.Concat(SumProperties), name =>
            Named(name, SumReaders) && IsCalled(name)
            || Named(name, SumProperties) && name.Parent is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax && !IsInvoked(name));

    private static List<(string File, string Member)> ScanAliases() =>
        Find([.. SumAliases, "Scopes", "RequestMembership"], name =>
            Named(name, SumAliases) && IsCalled(name)
            || name.Identifier.ValueText == "Scopes" && ReadsTheCredentialsScopes(name)
            || name.Identifier.ValueText == "Of" && IsCalled(name)
                && name.Parent is MemberAccessExpressionSyntax { Expression: var type }
                && (type as SimpleNameSyntax ?? (type as MemberAccessExpressionSyntax)?.Name)?.Identifier.ValueText == "RequestMembership");

    private static List<(string File, string Member)> Find(IEnumerable<string> names, Func<SimpleNameSyntax, bool> reads)
    {
        var any = names.ToList();
        return RepoSources.Under("src/API")
            .Where(source => any.Any(name => source.Text.Contains(name, StringComparison.Ordinal)))
            .SelectMany(source => source.Tree.GetRoot()
                .DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(reads)
                .Select(node => (source.Path, EnclosingMember.Of(node)))
                .Distinct())
            .ToList();
    }

    private static bool Named(SimpleNameSyntax name, string[] names) => names.Contains(name.Identifier.ValueText);

    private static ExpressionSyntax Use(SimpleNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name == name => access,
        MemberBindingExpressionSyntax binding => binding,
        _ => name,
    };

    // A property of the same name on a response (ShareLinkDto.CallerAccessEndsAt) is not a read of the sum.
    private static bool IsCalled(SimpleNameSyntax name) => IsInvoked(name) || Use(name).Parent is ArgumentSyntax;

    private static bool IsInvoked(SimpleNameSyntax name) =>
        Use(name) is var use && use.Parent is InvocationExpressionSyntax call && call.Expression == use;

    /// <summary>
    /// <c>Scopes</c> read off <c>GetAuthContext()</c>, directly, through <c>?.</c>, or through a local or
    /// pattern variable the member binds to it.
    /// </summary>
    private static bool ReadsTheCredentialsScopes(SimpleNameSyntax name)
    {
        var receiver = name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name => access.Expression,
            MemberBindingExpressionSyntax binding => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null,
        };
        if (receiver is null)
            return false;
        if (CallsGetAuthContext(receiver))
            return true;
        if (receiver is not IdentifierNameSyntax variable)
            return false;

        return name.FirstAncestorOrSelf<MemberDeclarationSyntax>()?.DescendantNodes().Any(node => node switch
        {
            VariableDeclaratorSyntax declarator => declarator.Identifier.ValueText == variable.Identifier.ValueText
                && declarator.Initializer is { } init && CallsGetAuthContext(init.Value),
            SingleVariableDesignationSyntax designation => designation.Identifier.ValueText == variable.Identifier.ValueText
                && designation.FirstAncestorOrSelf<IsPatternExpressionSyntax>() is { } pattern && CallsGetAuthContext(pattern.Expression),
            _ => false,
        }) == true;
    }

    private static bool CallsGetAuthContext(SyntaxNode node) =>
        node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => call.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == "GetAuthContext",
            IdentifierNameSyntax direct => direct.Identifier.ValueText == "GetAuthContext",
            _ => false,
        });
}
