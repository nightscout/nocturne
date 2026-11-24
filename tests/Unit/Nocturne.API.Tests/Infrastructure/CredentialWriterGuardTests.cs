using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.API.Multitenancy;
using Nocturne.API.Tests.Authorization;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A session or access token outlives its subject's deactivation by up to its lifetime, so a
/// controller action that mints a credential or a delegation refuses an inactive caller with
/// <c>IsCallerInactive</c>. The actions are found through <see cref="Writers"/> and
/// <see cref="RowWriters"/>, the methods and rows that create one, not by a list of actions: an
/// action whose body, or a helper of its controller it calls, reaches a writer must call
/// <c>IsCallerInactive</c> or be in <see cref="RefusedElsewhere"/> with the reason its product is
/// refused instead. <c>IsCallerInactive</c> reads the membership a tenant-resolved request carries
/// and is false on any other, so an action whose route is served without a tenant must call
/// <c>IsCallerSubjectInactiveAsync</c>, which reads the subject itself.
/// </summary>
/// <remarks>
/// A writer creates a new credential or delegation; one that redeems or edits an existing one
/// (accepting an invite, activating a guest code, exchanging an authorization code or a refresh
/// token, changing a grant's scopes) is not listed, because the minting action it follows already
/// refused. A sign-in ceremony and the enrolment of the caller's own factor mint for the subject
/// the factor names, and the services behind them refuse an inactive subject. The match is
/// syntactic: a receiver's type is the one its field, property, parameter or local declares, or the
/// type argument of the <c>GetRequiredService</c> that produced it, so a receiver reached any other
/// way is not seen. A call is not checked to come before the write it guards.
/// </remarks>
[Trait("Category", "Unit")]
public class CredentialWriterGuardTests
{
    private const string ControllersDirectory = "src/API/Nocturne.API/Controllers";
    private const string Refusal = "IsCallerInactive";
    private const string TenantlessRefusal = "IsCallerSubjectInactiveAsync";

    private const string Repositories = "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/";
    private const string ApiServices = "src/API/Nocturne.API/Services/";

    /// <summary>The methods that create a credential or a delegation, written <c>Type.Method</c>.</summary>
    private static readonly string[] Writers =
    [
        "IDirectGrantService.CreateAsync",
        "IOAuthGrantService.CreateOrUpdateGrantAsync",
        "IOAuthGrantRepository.CreateAsync",
        "IOAuthGrantRepository.AdoptLegacyCredentialAsync",
        "IOAuthDeviceCodeService.CreateDeviceCodeAsync",
        "IOAuthDeviceCodeService.ApproveDeviceCodeAsync",
        "IOAuthTokenService.GenerateAuthorizationCodeAsync",
        "IOAuthTokenRepository.StoreAuthorizationCodeAsync",
        "IOAuthTokenRepository.StoreRefreshTokenAsync",
        "IOAuthTokenRepository.StoreDeviceCodeAsync",
        "IOAuthTokenRepository.ApproveDeviceCodeAsync",
        "IGuestLinkService.CreateGuestLinkAsync",
        "IMemberInviteService.CreateInviteAsync",
        "IMemberInviteRepository.CreateAsync",
        "IShareLinkService.RotateAsync",
        "IShareLinkService.RevealAsync",
        "IDirectGrantService.KeepDeviceCredentialAsync",
        "IConnectorConfigurationService.SaveSecretsAsync",
        "IAuthorizationService.CreateSubjectAsync",
        "ITeamRepository.CreateInviteAsync",
        "ITeamRepository.CreateAccessLinkAsync",
        "IMembershipRequestService.ApproveRequestAsync",
        "IAccessRequestService.ApproveAsync",
        "ITenantCreationInviteService.CreateAsync",
        "ILoginCodeService.IssueAsync",
        "ILoginCodeService.IssueEmailedAsync",
        "TeamAccessService.ApproveAsync",
        "ChatIdentityService.ClaimPendingLinkAsync",
        "ITenantService.AddMemberAsync",
        "ITenantService.ProvisionWithOwnerAsync",
        "ITenantService.SetMemberAccessEndAsync",
        "ITenantMembershipRepository.AddMemberAsync",
        "ITenantMembershipRepository.SetDirectPermissionsAsync",
        "ITenantMembershipRepository.SetLimitTo24HoursAsync",
        "ITenantMembershipRepository.ReplaceRolesUnlessLastOwnerAsync",
        "ITenantRoleService.UpdateRoleAsync",
        "IAuthorizationService.UpdateRoleAsync",
        "IOAuthGrantService.UpdateGrantAsync",
        "IShareLinkService.SetScopesAsync",
        "IShareLinkService.SetFullHistoryAsync",
        "IMigrationJobService.StartMigrationAsync",
        "IJwtService.GenerateAccessToken",
    ];

    /// <summary>
    /// The rows that hold a credential or a delegation, each with the only files that construct one.
    /// A controller action that constructs one is a writer; a file not listed that constructs one
    /// is a writer this guard does not know, to be added to <see cref="Writers"/> and listed here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> RowWriters =
        new Dictionary<string, string[]>
        {
            ["OAuthGrantEntity"] =
            [
                "src/Infrastructure/Nocturne.Infrastructure.Data/Entities/OAuthGrantEntity.cs",
                Repositories + "OAuthGrantRepository.cs",
            ],
            ["OAuthAuthorizationCodeEntity"] = [Repositories + "OAuthTokenRepository.cs"],
            ["OAuthRefreshTokenEntity"] = [Repositories + "OAuthTokenRepository.cs"],
            ["OAuthDeviceCodeEntity"] = [Repositories + "OAuthTokenRepository.cs"],
            ["MemberInviteEntity"] = [Repositories + "MemberInviteRepository.cs"],
            ["TeamInviteEntity"] = [Repositories + "TeamRepository.cs"],
            ["TeamAccessLinkEntity"] = [Repositories + "TeamRepository.Access.cs"],
            ["TenantCreationInviteEntity"] = [Repositories + "TenantCreationInviteRepository.cs"],
            ["LoginCodeEntity"] = [ApiServices + "Auth/LoginCodeService.cs"],
            ["AlertInviteEntity"] = [ControllersDirectory + "/V4/Monitoring/AlertInvitesController.cs"],
            ["ChatIdentityDirectoryEntry"] = [ApiServices + "Chat/ChatIdentityDirectoryService.cs"],
            ["TenantMemberEntity"] =
            [
                "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DeviceSubjectFilter.cs",
                "src/Infrastructure/Nocturne.Infrastructure.Data/Services/MembershipReasons.cs",
                Repositories + "TenantMembershipRepository.cs",
            ],
            ["TenantMemberRoleEntity"] =
            [
                "src/Infrastructure/Nocturne.Infrastructure.Data/Services/MembershipReasons.cs",
                Repositories + "TenantMembershipRepository.cs",
            ],
            ["TeamTenantAccessEntity"] = [Repositories + "TeamRepository.Access.cs"],
        };

    /// <summary>
    /// Actions that reach a writer without calling the refusal, keyed <c>Controller.Action</c>, with
    /// the reason the caller's state does not matter.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RefusedElsewhere =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OAuthController.DeviceAuthorization"] =
                "an anonymous client's request mints a pending code that holds nothing until DeviceApprove, which refuses an inactive caller",
            ["CareLinkConnectController.Complete"] = "saves with no saver, so it adopts no Nightscout secret",
            ["AccessRequestController.Approve"] = PlatformAdminGate,
            ["TenantController.IssueLoginCode"] = PlatformAdminGate,
            ["TenantController.IssueRecoveryCode"] = PlatformAdminGate,
            ["TenantCreationInviteAdminController.Create"] = PlatformAdminGate,
            ["TenantDirectGrantController.Create"] = PlatformAdminGate,
            ["TenantController.AddMember"] = PlatformAdminGate,
            ["TenantController.Provision"] = PlatformAdminGate,
            ["DevAdminController.SeedTenant"] = DevOnlyGate,
            ["DevAdminController.EnterRecoveryMode"] = DevOnlyGate,
            ["SetupController.OwnerOptions"] = FirstRunGate,
            ["SetupController.OwnerOidc"] = FirstRunGate,
            ["MemberInviteController.StepDown"] = "it only narrows the caller's own roles to caretaker and gives nothing",
            ["PlatformAccessController.Access"] = PlatformAdminStanding,
        };

    private const string PlatformAdminGate =
        "the platform_admin role is read on every request and needs an active subject, so an inactive one is refused at the gate";

    private const string PlatformAdminStanding =
        "it mints only for a caller whose platform_admin standing this request read, which needs an active subject, "
        + "and PlatformAccessCookieHandler reads that standing again on every use of what it mints";

    private const string DevOnlyGate =
        "it lives in the .DevOnly namespace, which is dropped from controller discovery outside Development";

    private const string FirstRunGate =
        "it makes the instance's first owner and refuses once the instance has one; the caller is an anonymous visitor with no earlier subject to deactivate";

    private const string OwnerExistsCheck = "TenantHasCredentialedMemberAsync";

    private const string SetupRoutePrefix = "/api/v4/setup/";

    private static readonly IReadOnlyList<MintingAction> Actions =
        RepoSources.Under(ControllersDirectory).SelectMany(file => ActionsIn(file.Path, file.Tree)).ToList();

    private static IEnumerable<MintingAction> Minting => Actions.Where(a => a.Reached.Count > 0);

    private static IEnumerable<MintingAction> Required => Minting.Where(a => !RefusedElsewhere.ContainsKey(a.Key));

    [Fact]
    public void EveryActionThatMintsACredentialRefusesAnInactiveCaller()
    {
        var unguarded = Required
            .Select(a => (Action: a, Tenantless: TenantlessRequests(a).ToList()))
            .Where(a => IsUnguarded(a.Action, a.Tenantless.Count > 0))
            .Select(a => $"{a.Action.Key} (reaches {string.Join(", ", a.Action.Reached)}) needs "
                + (a.Tenantless.Count > 0 ? $"{TenantlessRefusal}: {string.Join(", ", a.Tenantless)}" : Refusal))
            .ToList();

        unguarded.Should().BeEmpty(
            "a deactivated subject's session still authenticates, so an action that mints from it refuses "
            + "that caller first: with HttpContext.IsCallerInactive, which reads the membership a "
            + "tenant-resolved request carries, or with HttpContext.IsCallerSubjectInactiveAsync where the "
            + "route is served without a tenant and IsCallerInactive is always false. Or name it in "
            + "RefusedElsewhere with the reason its product is refused instead. Unguarded:\n  "
            + string.Join("\n  ", unguarded));
    }

    [Fact]
    public void EveryRefusedElsewhereEntryStillMintsWithoutTheRefusal()
    {
        var byKey = Minting.ToLookup(a => a.Key);

        foreach (var (key, reason) in RefusedElsewhere)
        {
            byKey[key].Should().NotBeEmpty("{0} (\"{1}\") no longer reaches a writer: drop the entry", key, reason);
            byKey[key].Should().OnlyContain(
                a => a.Refusals.Count == 0, "{0} now refuses an inactive caller: drop the entry", key);
        }
    }

    [Fact]
    public void EveryPlatformAdminEntryIsGatedOnThatRole()
    {
        var ungated = RefusedElsewhere
            .Where(entry => entry.Value == PlatformAdminGate)
            .Select(entry => entry.Key.Split('.'))
            .Where(key => ControllerActionReflection.GetControllers()
                .Where(controller => controller.Name == key[0])
                .DefaultIfEmpty()
                .Any(controller => controller is null || !PlatformAdminOnly(controller, key[1])))
            .Select(key => string.Join('.', key))
            .ToList();

        ungated.Should().BeEmpty("the reason holds only where [Authorize(Roles = \"platform_admin\")] stands in front of the action");
    }

    [Fact]
    public void EveryPlatformAdminStandingEntryReadsThatStanding()
    {
        var entries = RefusedElsewhere.Where(entry => entry.Value == PlatformAdminStanding).Select(entry => entry.Key).ToList();

        var unread = entries
            .Where(key => !RepoSources.Under(ControllersDirectory)
                .SelectMany(file => file.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                .Where(type => type.Identifier.ValueText == key.Split('.')[0])
                .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>())
                .Where(method => method.Identifier.ValueText == key.Split('.')[1])
                .Any(method => method.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                    .Any(access => access.Name.Identifier.ValueText == "IsPlatformAdmin")))
            .ToList();

        entries.Should().NotBeEmpty();
        unread.Should().BeEmpty("the reason holds only for an action that reads the caller's IsPlatformAdmin");
    }

    [Fact]
    public void EveryDevOnlyEntryLivesInTheDevOnlyNamespace()
    {
        var elsewhere = RefusedElsewhere
            .Where(entry => entry.Value == DevOnlyGate)
            .Select(entry => entry.Key)
            .Where(key => !Minting.Any(a => a.Key == key && a.Namespace.EndsWith(".DevOnly", StringComparison.Ordinal)))
            .ToList();

        elsewhere.Should().BeEmpty("the reason holds only for a controller in the .DevOnly namespace");
    }

    [Fact]
    public void EveryFirstRunEntryRefusesOnceTheInstanceHasAnOwner()
    {
        var entries = RefusedElsewhere.Where(entry => entry.Value == FirstRunGate).Select(entry => entry.Key).ToList();
        var minting = Minting.ToLookup(a => a.Key);

        var unpinned = entries
            .Where(key => !key.StartsWith("SetupController.", StringComparison.Ordinal)
                || !minting[key].Any()
                || minting[key].Any(a => !a.Invoked.Contains(OwnerExistsCheck)
                    || TenantlessRequests(a, anyRoute: true).Any(request => !request.Contains(SetupRoutePrefix, StringComparison.Ordinal))))
            .ToList();

        entries.Should().NotBeEmpty();
        unpinned.Should().BeEmpty(
            "the reason holds only for a SetupController action under {0} that reaches {1}, the check that "
            + "refuses once the instance has an owner", SetupRoutePrefix, OwnerExistsCheck);
    }

    [Fact]
    public void EveryWriterIsDeclared()
    {
        var declared = RepoSources.Src
            .SelectMany(file => file.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()
                .Select(method => $"{type.Identifier.ValueText}.{method.Identifier.ValueText}"))
            .ToHashSet(StringComparer.Ordinal);

        Writers.Where(writer => !declared.Contains(writer)).Should().BeEmpty(
            "a renamed writer would leave the guard matching nothing");
    }

    [Fact]
    public void EveryCredentialRowIsBuiltByANamedWriter()
    {
        var built = RepoSources.Src
            .SelectMany(file => file.Tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                .Select(creation => CreatedType(creation))
                .Where(name => name is not null && RowWriters.ContainsKey(name))
                .Select(name => $"{name} in {file.Path}"))
            .Distinct()
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        var expected = RowWriters
            .SelectMany(row => row.Value.Select(path => $"{row.Key} in {path}"))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        built.Should().Equal(
            expected,
            "a row built anywhere else is a writer Writers does not name, and a file listed that no longer "
            + "builds one would silently cover a future writer there");
    }

    [Fact]
    public void TheDiscoveryFindsTheMintingActions()
    {
        Actions.Should().Contain(a => a.Type == "DirectGrantController" && a.Name == "Create" && a.Refusals.Contains(Refusal));

        Minting.Select(a => a.Key).Should().Contain(
        [
            "AlertInvitesController.CreateInvite",
            "ConfigurationController.SaveSecrets",
            "GuestLinkController.CreateGuestLink",
            "ShareLinkController.RotateShareLink",
            "ChatIdentityController.ClaimLink",
            "MemberInviteController.SetMemberRoles",
            "OAuthController.UpdateGrant",
            "MigrationController.StartMigration",
            "CareLinkConnectController.DesktopToken",
            "PlatformAccessController.Access",
        ]);

        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/teams/{teamId}/invites", "POST")
            .Should().BeTrue("the tenantless check has to be able to see a path it must refuse");
    }

    [Theory]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { await _g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C(IDirectGrantService g) { [HttpPost] public async Task A() { await g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { IDirectGrantService G { get; } [HttpPost] public async Task A() { await this.G.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { [HttpPost] public async Task A([FromServices] IDirectGrantService g) { await g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { IDirectGrantService? _g; [HttpPost] public async Task A() { await _g!.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { [HttpPost] public async Task A() { await HttpContext.RequestServices.GetRequiredService<IDirectGrantService>().CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { [HttpPost] public async Task A() { var g = HttpContext.RequestServices.GetRequiredService<IDirectGrantService>(); await g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { IOAuthDeviceCodeService _d; [HttpPost] public async Task A() { var r = _d.CreateDeviceCodeAsync(c, s); } }",
        "IOAuthDeviceCodeService.CreateDeviceCodeAsync")]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { await Mint(); } async Task Mint() { await _g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { await this.Mint(); } async Task Mint() { await _g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { Func<Task> f = () => _g.CreateAsync(); } }",
        "IDirectGrantService.CreateAsync")]
    [InlineData("class C { IJwtService _j; [HttpPost] public IActionResult A() { var t = _j.GenerateAccessToken(s, [], [], scopes: [x], tenantId: id); } }",
        "IJwtService.GenerateAccessToken")]
    [InlineData("class C(IJwtService jwtService) { [HttpGet] public IActionResult A() { var g = jwtService.GenerateAccessToken(s, [], [], []); } }",
        "IJwtService.GenerateAccessToken")]
    [InlineData("class C { [HttpPost] public async Task A() { var invite = new AlertInviteEntity { Id = id }; } }",
        "AlertInviteEntity")]
    [InlineData("class C { [HttpPost] public async Task A() { AlertInviteEntity invite = new() { Id = id }; } }",
        "AlertInviteEntity")]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { await _g.RevokeAsync(); } }", "")]
    [InlineData("class C { IContextFactory _f; [HttpPost] public async Task A() { await _f.CreateAsync(); } }", "")]
    [InlineData("class C { IJwtService _j; [HttpPost] public IActionResult A() { var v = _j.ValidateAccessToken(t); } }", "")]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { await other.CreateAsync(); } }", "")]
    [InlineData("class C { IDirectGrantService _g; public async Task A() { await _g.CreateAsync(); } }", "")]
    [InlineData("class C { IDirectGrantService _g; [HttpPost] public async Task A() { await Elsewhere.Mint(); } }", "")]
    public void FindsTheWritersAnActionReaches(string source, string expected)
    {
        var actions = ActionsIn("C.cs", CSharpSyntaxTree.ParseText(source));

        string.Join(", ", actions.SelectMany(a => a.Reached)).Should().Be(expected);
    }

    [Theory]
    [InlineData("class C { [HttpPost] public async Task A() { if (HttpContext.IsCallerInactive()) return; } }", "IsCallerInactive")]
    [InlineData("class C { [HttpPost] public async Task A() { if (adopts && HttpContext.IsCallerInactive()) return; } }", "IsCallerInactive")]
    [InlineData("class C { [HttpPost] public async Task A() { if (Guard()) return; } bool Guard() => HttpContext.IsCallerInactive(); }", "IsCallerInactive")]
    [InlineData("class C { [HttpPost] public async Task A() { if (IsCallerInactive(HttpContext)) return; } }", "IsCallerInactive")]
    [InlineData("class C { [HttpPost] public async Task A(CancellationToken ct) { if (await HttpContext.IsCallerSubjectInactiveAsync(ct)) return; } }", "IsCallerSubjectInactiveAsync")]
    [InlineData("class C { [HttpPost] public async Task A() { } bool Unused() => HttpContext.IsCallerInactive(); }", "")]
    [InlineData("class C { [HttpPost] public async Task A() { var inactive = false; } }", "")]
    public void FindsTheRefusal(string source, string expected)
    {
        string.Join(", ", ActionsIn("C.cs", CSharpSyntaxTree.ParseText(source)).Single().Refusals).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, "IsCallerInactive", false)]
    [InlineData(false, "IsCallerSubjectInactiveAsync", true)]
    [InlineData(false, "", true)]
    [InlineData(true, "IsCallerSubjectInactiveAsync", false)]
    [InlineData(true, "IsCallerInactive", true)]
    [InlineData(true, "", true)]
    public void AnActionNeedsTheRefusalItsRouteCanSee(bool tenantless, string calls, bool unguarded)
    {
        var action = new MintingAction(
            "C.cs", "N", "C", "A", ["IDirectGrantService.CreateAsync"], calls.Length == 0 ? [] : [calls]);

        IsUnguarded(action, tenantless).Should().Be(unguarded);
    }

    private static bool IsUnguarded(MintingAction action, bool tenantless) =>
        !action.Refusals.Contains(tenantless ? TenantlessRefusal : Refusal);

    private static bool PlatformAdminOnly(Type controller, string actionName) =>
        controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(controller.GetMethods().Where(method => method.Name == actionName)
                .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>(inherit: true)))
            .Any(authorize => authorize.Roles == "platform_admin");

    private static IEnumerable<string> TenantlessRequests(MintingAction action, bool anyRoute = false)
    {
        var controller = ControllerActionReflection.GetControllers()
            .SingleOrDefault(type => type.FullName == $"{action.Namespace}.{action.Type}");
        controller.Should().NotBeNull("{0} is a public controller the route reflection can see", action.Key);

        return ControllerActionReflection.GetActionMethods(controller!)
            .Where(method => method.Name == action.Name)
            .SelectMany(method => ControllerActionReflection.GetRoutes(controller!, method)
                .SelectMany(route => ControllerActionReflection.GetHttpMethods(method)
                    .Where(verb => anyRoute || TenantResolutionMiddleware.IsTenantlessAllowed(route, verb))
                    .Select(verb => $"{verb} {route}")));
    }

    private static List<MintingAction> ActionsIn(string path, SyntaxTree tree)
    {
        var found = new List<MintingAction>();

        foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var methods = type.Members.OfType<MethodDeclarationSyntax>().ToList();
            var shared = ClassScope(type);

            foreach (var action in methods.Where(IsAction))
            {
                var reached = Reach(action, methods);
                found.Add(new MintingAction(
                    path,
                    type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "",
                    type.Identifier.ValueText,
                    action.Identifier.ValueText,
                    reached.SelectMany(method => WritersIn(method, shared)).Distinct().Order().ToList(),
                    reached.SelectMany(method => method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        .Select(InvokedName)
                        .OfType<string>()
                        .Distinct()
                        .Order()
                        .ToList()));
            }
        }

        return found;
    }

    private static bool IsAction(MethodDeclarationSyntax method) =>
        method.Modifiers.Any(SyntaxKind.PublicKeyword)
        && method.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => RightmostName(attribute.Name) is "HttpGet" or "HttpPost" or "HttpPut" or "HttpPatch"
                or "HttpDelete" or "HttpHead" or "HttpOptions");

    /// <summary>The action and every method of its class it calls by name, however deep.</summary>
    private static List<MethodDeclarationSyntax> Reach(
        MethodDeclarationSyntax action, IReadOnlyList<MethodDeclarationSyntax> methods)
    {
        var reached = new List<MethodDeclarationSyntax>();
        var pending = new Queue<MethodDeclarationSyntax>([action]);

        while (pending.TryDequeue(out var method))
        {
            if (reached.Contains(method))
                continue;

            reached.Add(method);

            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (CalledOnThis(call) is not { } name)
                    continue;

                foreach (var helper in methods.Where(m => m.Identifier.ValueText == name))
                    pending.Enqueue(helper);
            }
        }

        return reached;
    }

    private static string? CalledOnThis(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var name } => name.Identifier.ValueText,
        _ => null,
    };

    private static IEnumerable<string> WritersIn(MethodDeclarationSyntax method, IReadOnlyDictionary<string, string> shared)
    {
        var scope = shared.Concat(DeclaredTypes(method)).ToLookup(pair => pair.Key, pair => pair.Value);

        foreach (var node in method.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
                    when ReceiverType(access.Expression, scope) is { } receiver
                        && Writers.Contains($"{receiver}.{access.Name.Identifier.ValueText}"):
                    yield return $"{receiver}.{access.Name.Identifier.ValueText}";
                    break;

                case BaseObjectCreationExpressionSyntax creation
                    when CreatedType(creation) is { } created && RowWriters.ContainsKey(created):
                    yield return created;
                    break;
            }
        }
    }

    private static string? CreatedType(BaseObjectCreationExpressionSyntax creation) => creation switch
    {
        ObjectCreationExpressionSyntax { Type: var type } => TypeName(type),
        ImplicitObjectCreationExpressionSyntax
            { Parent: EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } } =>
            TypeName(declaration.Type),
        _ => null,
    };

    private static string? ReceiverType(ExpressionSyntax receiver, ILookup<string, string> scope) => receiver switch
    {
        ParenthesizedExpressionSyntax parenthesized => ReceiverType(parenthesized.Expression, scope),
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed =>
            ReceiverType(suppressed.Operand, scope),
        IdentifierNameSyntax name => scope[name.Identifier.ValueText].FirstOrDefault(),
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var name } =>
            scope[name.Identifier.ValueText].FirstOrDefault(),
        InvocationExpressionSyntax call => ResolvedFromServices(call),
        _ => null,
    };

    private static string? ResolvedFromServices(InvocationExpressionSyntax call) =>
        (call.Expression is MemberAccessExpressionSyntax { Name: var member } ? member : call.Expression as SimpleNameSyntax)
            is GenericNameSyntax { Identifier.ValueText: "GetRequiredService" or "GetService" } generic
        && generic.TypeArgumentList.Arguments.Count == 1
            ? TypeName(generic.TypeArgumentList.Arguments[0])
            : null;

    /// <summary>The types a class declares for its fields, properties and primary-constructor parameters.</summary>
    private static Dictionary<string, string> ClassScope(TypeDeclarationSyntax type)
    {
        var scope = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in type.ParameterList?.Parameters ?? [])
        {
            if (parameter.Type is not null)
                scope[parameter.Identifier.ValueText] = TypeName(parameter.Type);
        }

        foreach (var member in type.Members.Where(m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax))
        {
            foreach (var (name, typeName) in DeclaredTypes(member))
                scope[name] = typeName;
        }

        return scope;
    }

    /// <summary>The names a node declares with an explicit type, or with <c>var</c> from a <c>GetRequiredService</c>.</summary>
    private static IEnumerable<KeyValuePair<string, string>> DeclaredTypes(SyntaxNode node)
    {
        foreach (var declared in node.DescendantNodesAndSelf())
        {
            switch (declared)
            {
                case ParameterSyntax { Type: { } type } parameter:
                    yield return new(parameter.Identifier.ValueText, TypeName(type));
                    break;

                case PropertyDeclarationSyntax property:
                    yield return new(property.Identifier.ValueText, TypeName(property.Type));
                    break;

                case VariableDeclarationSyntax declaration:
                    foreach (var variable in declaration.Variables)
                    {
                        var typeName = declaration.Type.IsVar
                            ? variable.Initializer?.Value is InvocationExpressionSyntax call ? ResolvedFromServices(call) : null
                            : TypeName(declaration.Type);

                        if (typeName is not null)
                            yield return new(variable.Identifier.ValueText, typeName);
                    }

                    break;
            }
        }
    }

    private static string TypeName(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => TypeName(nullable.ElementType),
        QualifiedNameSyntax qualified => TypeName(qualified.Right),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static string? InvokedName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    private static string? RightmostName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    /// <summary>A controller action, the writers it reaches and whether it calls the refusal.</summary>
    private sealed record MintingAction(
        string Path, string Namespace, string Type, string Name, IReadOnlyList<string> Reached, IReadOnlyList<string> Invoked)
    {
        public string Key => $"{Type}.{Name}";

        public IReadOnlyList<string> Refusals => [.. Invoked.Where(name => name is Refusal or TenantlessRefusal)];
    }
}
