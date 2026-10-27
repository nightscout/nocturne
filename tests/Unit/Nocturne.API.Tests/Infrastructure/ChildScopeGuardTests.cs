using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A DI scope opened in code reads as no one: not a share and not history-clamped, and the clamp fails
/// open. Work done for a caller opens its scope with
/// <see cref="TenantJobScope.OpenFor(IServiceProvider, TenantReader, string)"/>, which carries the
/// caller's <see cref="TenantReader"/>, and is not checked here. Every other scope is limited to the
/// members listed here, each for the reason its group gives: a raw <c>CreateScope</c> or
/// <c>CreateAsyncScope</c>, a <see cref="TenantJobScope.Open(IServiceProvider, TenantContext?, string)"/>,
/// or a use of a method, property or field that returns a scope it opened one of those ways, so a helper does not hide
/// its callers. An entry names the file and the member the call sits in, so a new method in a listed
/// file is not covered by its neighbour.
/// </summary>
/// <remarks>
/// The match is syntactic. It follows a qualified or <c>global::</c> name, a <c>using</c> alias of
/// <c>TenantJobScope</c> and a <c>using static</c> of it or of the scope extensions. It does not follow
/// a delegate or a reflection call that names none of these.
/// </remarks>
[Trait("Category", "Unit")]
public class ChildScopeGuardTests
{
    private const string JobScope = nameof(TenantJobScope);

    private const string Seam = "src/Infrastructure/Nocturne.Infrastructure.Data/Services/TenantJobScope.cs";

    private static readonly string[] ScannedTrees = ["src"];

    private static readonly string[] RawScopeMethods = ["CreateScope", "CreateAsyncScope"];

    private static readonly string[] Allowed =
    [
        // The seam.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/TenantJobScope.cs: TenantJobScope.Open",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/TenantJobScope.cs: TenantJobScope.OpenFor",

        // Startup and hosted services: no caller is waiting on them.
        "src/API/Nocturne.API/Program.cs: GlobalStatement",
        "src/API/Nocturne.API/Hubs/HubConnectionRevalidator.cs: HubConnectionRevalidator.RevalidateAsync",
        "src/API/Nocturne.API/Services/Alerts/AlertRuleConditionAuditService.cs: AlertRuleConditionAuditService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Alerts/AlertSweepService.cs: AlertSweepService.BeginTenantScope",
        "src/API/Nocturne.API/Services/Alerts/AlertSweepService.cs: AlertSweepService.CheckSnoozedInstancesAsync",
        "src/API/Nocturne.API/Services/Alerts/AlertSweepService.cs: AlertSweepService.CloseHysteresisWindowsAsync",
        "src/API/Nocturne.API/Services/Alerts/AlertSweepService.cs: AlertSweepService.EvaluateAutoResolveAsync",
        "src/API/Nocturne.API/Services/Alerts/AlertSweepService.cs: AlertSweepService.EvaluateWallClockRulesAsync",
        "src/API/Nocturne.API/Services/Alerts/AlertSweepService.cs: AlertSweepService.ProcessTenantSnoozesAsync",
        "src/API/Nocturne.API/Services/Alerts/RuleScopeClassBackfillService.cs: RuleScopeClassBackfillService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Auth/AuthorizationSeedService.cs: AuthorizationSeedService.StartAsync",
        "src/API/Nocturne.API/Services/Auth/OidcProviderService.cs: OidcProviderService.SyncConfigProvidersAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/ApsSnapshotCurvesBackfillService.cs: ApsSnapshotCurvesBackfillService.ExecuteAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/CompressionLowDetectionService.cs: CompressionLowDetectionService.ScanReadyNightsAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/ConnectorBackgroundService.cs: ConnectorBackgroundService.SyncForTenantAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/DeduplicationReconciliationBackgroundService.cs: DeduplicationReconciliationBackgroundService.ReconcileAllTenantsAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/DemoServiceHealthMonitor.cs: DemoServiceHealthMonitor.CleanupDemoDataAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/NightscoutConnectorBackgroundService.cs: NightscoutConnectorBackgroundService.StartListenerForTenantAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/NocturneRemoteConnectorBackgroundService.cs: NocturneRemoteConnectorBackgroundService.StartRealtimeListenersAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/NotificationCleanupService.cs: NotificationCleanupService.ExecuteAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/TempBasalOriginCorroborationService.cs: TempBasalOriginCorroborationService.RunAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/TempBasalRateSweepService.cs: TempBasalRateSweepService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Chat/ChatIdentityPendingLinkCleanupService.cs: ChatIdentityPendingLinkCleanupService.SweepAsync",
        "src/API/Nocturne.API/Services/Compatibility/DiscrepancyMaintenanceService.cs: DiscrepancyMaintenanceService.PerformMaintenanceAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthWorker.cs: GoogleHealthWorker.ProcessRequestsAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthWorker.cs: GoogleHealthWorker.SyncTenantAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/PostgresGoogleHealthGrantHolders.cs: PostgresGoogleHealthGrantHolders.AdoptAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationStartupService.cs: MigrationStartupService.NotifyIfPendingAsync",
        "src/API/Nocturne.API/Services/Monitoring/TrackerAlertRuleBackfillService.cs: TrackerAlertRuleBackfillService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Notifications/NotificationResolutionService.cs: NotificationResolutionService.EvaluateAllTenantsAsync",
        "src/API/Nocturne.API/Services/Platform/InstanceKey/InstanceKeyChangeService.cs: InstanceKeyChangeService.ReconcilePlatformAsync",
        "src/API/Nocturne.API/Services/Platform/InstanceKey/InstanceKeyChangeService.cs: InstanceKeyChangeService.ReconcileTenantAsync",
        "src/API/Nocturne.API/Services/Platform/Releases/ReleaseNoticeService.cs: ReleaseNoticeService.NotifyOwnersAsync",

        // Credential and identity lookups: subjects, grants, memberships and guest links, which no
        // category or history policy narrows.
        "src/API/Nocturne.API/Hubs/HubCredentialVerifier.cs: HubCredentialVerifier.GuestSessionAsync",
        "src/API/Nocturne.API/Middleware/AuthenticationMiddleware.cs: AuthenticationMiddleware.InvokeAsync",
        "src/API/Nocturne.API/Middleware/Handlers/GuestSessionHandler.cs: GuestSessionHandler.AuthenticateAsync",
        "src/API/Nocturne.API/Middleware/Handlers/LegacyJwtHandler.cs: LegacyJwtHandler.AuthenticateAsync",
        "src/API/Nocturne.API/Middleware/Handlers/OAuthAccessTokenHandler.cs: OAuthAccessTokenHandler.AuthenticateAsync",
        "src/API/Nocturne.API/Middleware/Handlers/OidcTokenHandler.cs: OidcTokenHandler.AuthenticateAsync",
        "src/API/Nocturne.API/Middleware/Handlers/OidcTokenHandler.cs: OidcTokenHandler.RecordLastLoginAsync",
        "src/API/Nocturne.API/Middleware/Handlers/PlatformAccessCookieHandler.cs: PlatformAccessCookieHandler.AuthenticateAsync",
        "src/API/Nocturne.API/Middleware/Handlers/SessionCookieHandler.cs: SessionCookieHandler.AuthenticateAsync",

        // Job and connector bookkeeping: job runs, migration sources, sync cursors and locks, none of
        // them time-series.
        "src/API/Nocturne.API/Services/Connectors/ConnectorCursorResetJobService.cs: ConnectorCursorResetJobService.FindRecordAsync",
        "src/API/Nocturne.API/Services/Connectors/ConnectorCursorResetJobService.cs: ConnectorCursorResetJobService.StartResetAsync",
        "src/API/Nocturne.API/Services/Connectors/ConnectorCursorResetJobService.cs: ConnectorResetJob.PersistSnapshotAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthCoordinator.cs: GoogleHealthCoordinator.PendingTenantsAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthCoordinator.cs: GoogleHealthCoordinator.ReadAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthCoordinator.cs: GoogleHealthCoordinator.WriteAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.PersistSnapshotAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.FindRunAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.GetHistoryAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.GetSourcesAsync",

        // Work a request starts that returns nothing it read. It writes as the system, and the caller
        // reads what it wrote back under the caller's own policies. Read as the caller, a sync or an
        // inference would take the rows the caller cannot see for missing ones.
        "src/API/Nocturne.API/Controllers/V4/DevOnly/DevAdminController.cs: DevAdminController.SyncAll",
        "src/API/Nocturne.API/Middleware/CompatibilityProxyMiddleware.cs: CompatibilityProxyMiddleware.InvokeAsync",
        "src/API/Nocturne.API/Middleware/Handlers/ApiKeyHandler.cs: ApiKeyHandler.SendRotationNudgeAsync",
        "src/API/Nocturne.API/Services/Auth/ShareLinkRotatedNotifier.cs: ShareLinkRotatedNotifier.NotifyAsync",
        "src/API/Nocturne.API/Services/Connectors/ConnectorCursorResetJobService.cs: ConnectorResetJob.ExecuteAsync",
        "src/API/Nocturne.API/Services/Connectors/ConnectorSyncService.cs: ConnectorSyncService.TriggerSyncAsync",
        "src/API/Nocturne.API/Services/Devices/SensorSessionInferenceRunner.cs: SensorSessionInferenceRunner.RunAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.CreateTenantScope",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.ExecuteApiMigrationAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.ExecuteMongoMigrationAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.MigrateMongoDecomposedCollectionAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.MigratePagedCollectionAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.MigrateProfilesViaApiAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.TransformDeviceStatusAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.TransformProfileAsync",
        "src/API/Nocturne.API/Services/Seeding/SampleDataSeeder.cs: SampleDataSeeder.SeedInTenantScopeAsync",
        "src/API/Nocturne.API/Services/V4/LoopClosedLoopStamper.cs: LoopClosedLoopStamper.RunAsync",
        "src/API/Nocturne.API/Services/V4/TempBasalRateReresolver.cs: TempBasalRateReresolver.RunAsync",
    ];

    private static readonly IReadOnlyList<string> Openers = Scan();

    [Fact]
    public void OnlyTheAllowlistOpensAScopeThatReadsAsNoOne()
    {
        Openers.Except(Allowed).Should().BeEmpty(
            "work done for a caller opens its scope with TenantJobScope.OpenFor(TenantReader.Of(...)); "
            + "allowlist a scope that reads as no one here deliberately, under the reason it fits");
    }

    [Fact]
    public void EveryAllowlistEntryStillOpensOne()
    {
        Allowed.Except(Openers).Should().BeEmpty("a stale entry would silently cover a future scope in that member");
    }

    [Theory]
    [InlineData("class C { void M(IServiceProvider p) { using var s = p.CreateScope(); } }", "C.M")]
    [InlineData("class C { async Task M(IServiceScopeFactory f) { await using var s = f.CreateAsyncScope(); } }", "C.M")]
    [InlineData("class C { void M(IServiceProvider? p) { using var s = p?.CreateScope(); } }", "C.M")]
    [InlineData("class C { void M() { var s = TenantJobScope.Open(p, t, \"a\"); } }", "C.M")]
    [InlineData("class C { void M() { var s = Nocturne.Infrastructure.Data.Services.TenantJobScope.Open(p, t, \"a\"); } }", "C.M")]
    [InlineData("class C { void M() { var s = global::Nocturne.Infrastructure.Data.Services.TenantJobScope.Open(p, t, \"a\"); } }", "C.M")]
    [InlineData("using Jobs = Nocturne.Infrastructure.Data.Services.TenantJobScope; class C { void M() { var s = Jobs.Open(p, t, \"a\"); } }", "C.M")]
    [InlineData("using static Nocturne.Infrastructure.Data.Services.TenantJobScope; class C { void M() { var s = Open(p, t, \"a\"); } }", "C.M")]
    [InlineData("using static Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions; class C { void M() { using var s = CreateScope(p); } }", "C.M")]
    [InlineData("class C { void M() { using var s = ServiceProviderServiceExtensions.CreateScope(p); } }", "C.M")]
    [InlineData("class C { void M() => Use(p.CreateScope); }", "C.M")]
    [InlineData("class C { void M() { Task.Run(async () => { await using var s = f.CreateAsyncScope(); }); } }", "C.M")]
    [InlineData("class O { class C { IServiceScope P => p.CreateScope(); } }", "O.C.P")]
    [InlineData("class C { IServiceScope Helper() => p.CreateScope(); void M() { using var s = Helper(); } }", "C.Helper,C.M")]
    [InlineData("class C { AsyncServiceScope Helper() => TenantJobScope.Open(p, t, \"a\"); void M() { using var s = this.Helper(); } }", "C.Helper,C.M")]
    [InlineData("class C { IServiceScope Scope => p.CreateScope(); void M() { using var s = Scope; } }", "C.Scope,C.M")]
    [InlineData("class C { static readonly Func<IServiceScope> Make = () => p.CreateScope(); void M() { using var s = Make(); } }", "C.Make,C.M")]
    [InlineData("class C { IServiceScope Helper() => p.CreateScope(); } class D { void M() { var s = Helper(); } }", "C.Helper")]
    [InlineData("class C { IServiceScope Helper() => p.CreateScope(); } class D { void M(C c) { using var s = c.Helper(); } }", "C.Helper,D.M")]
    [InlineData("class C { void M() { var s = TenantJobScope.OpenFor(p, r, \"a\"); } }", null)]
    [InlineData("class C { void M() { var s = File.Open(path); } }", null)]
    [InlineData("class C { void M() { var s = Open(path); } }", null)]
    public void FindsTheOpeningMember(string source, string? expected)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var members = OpeningMembers(tree, ScopeHelpers([tree]));

        if (expected is null)
            members.Should().BeEmpty();
        else
            members.Should().Equal(expected.Split(','));
    }

    /// <summary>
    /// Methods, properties and fields outside the seam that return a scope opened by an opener in their
    /// body or initializer, by name, with the types declaring them; found to a fixed point so a helper
    /// over a helper is one too.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ScopeHelpers(IReadOnlyList<SyntaxTree> trees)
    {
        var helpers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        bool grew;
        do
        {
            grew = false;
            foreach (var tree in trees)
            {
                var file = FileNames.Of(tree.GetRoot());
                foreach (var (name, type, body) in ScopeReturningMembers(tree.GetRoot()))
                {
                    if (!OpeningNodes(body, file, helpers).Any())
                        continue;

                    if (!helpers.TryGetValue(name, out var types))
                        helpers[name] = types = new HashSet<string>(StringComparer.Ordinal);
                    grew |= types.Add(type);
                }
            }
        }
        while (grew);

        return helpers;
    }

    private static IEnumerable<(string Name, string DeclaringType, SyntaxNode Body)> ScopeReturningMembers(SyntaxNode root)
    {
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var declaringType = member.Parent is BaseTypeDeclarationSyntax type ? type.Identifier.ValueText : null;
            if (declaringType is null)
                continue;

            switch (member)
            {
                case MethodDeclarationSyntax method when ReturnsScope(method.ReturnType):
                    yield return (method.Identifier.ValueText, declaringType, method);
                    break;
                case PropertyDeclarationSyntax property when ReturnsScope(property.Type):
                    yield return (property.Identifier.ValueText, declaringType, property);
                    break;
                case FieldDeclarationSyntax field when ReturnsScope(field.Declaration.Type):
                    foreach (var variable in field.Declaration.Variables)
                        yield return (variable.Identifier.ValueText, declaringType, variable);
                    break;
            }
        }
    }

    private static bool ReturnsScope(TypeSyntax type) =>
        type.ToString().Contains("ServiceScope", StringComparison.Ordinal);

    private static List<string> OpeningMembers(SyntaxTree tree, Dictionary<string, HashSet<string>> helpers) =>
        OpeningNodes(tree.GetRoot(), FileNames.Of(tree.GetRoot()), helpers)
            .Select(EnclosingMember.Of)
            .Distinct()
            .ToList();

    /// <summary>
    /// Every mention by name of an opener in <paramref name="node"/>: a call, a conditional call or a
    /// method group of a raw scope method, every <c>TenantJobScope.Open</c> however it is named, and
    /// every use of a helper: through a member access by its name (the receiver's type is not known to
    /// syntax), or by its bare name inside a type declaring it.
    /// </summary>
    private static IEnumerable<SyntaxNode> OpeningNodes(
        SyntaxNode node, FileNames file, Dictionary<string, HashSet<string>> helpers) =>
        node.DescendantNodes().Where(candidate => candidate switch
        {
            MemberAccessExpressionSyntax member =>
                IsRawOrHelper(member.Name.Identifier.ValueText, helpers)
                || (member.Name.Identifier.ValueText == nameof(TenantJobScope.Open)
                    && file.NamesJobScope(member.Expression)),
            MemberBindingExpressionSyntax binding => IsRawOrHelper(binding.Name.Identifier.ValueText, helpers),
            IdentifierNameSyntax name when name.Parent is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax
                or QualifiedNameSyntax or AliasQualifiedNameSyntax or UsingDirectiveSyntax) =>
                RawScopeMethods.Contains(name.Identifier.ValueText)
                || (helpers.TryGetValue(name.Identifier.ValueText, out var types)
                    && name.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any(t => types.Contains(t.Identifier.ValueText)))
                || (name.Identifier.ValueText == nameof(TenantJobScope.Open) && file.ImportsJobScope),
            _ => false,
        });

    private static bool IsRawOrHelper(string name, Dictionary<string, HashSet<string>> helpers) =>
        RawScopeMethods.Contains(name) || helpers.ContainsKey(name);

    /// <summary>How one file can name <c>TenantJobScope</c>: by its name, an alias, or a <c>using static</c>.</summary>
    private sealed record FileNames(IReadOnlySet<string> JobScopeNames, bool ImportsJobScope)
    {
        public static FileNames Of(SyntaxNode root)
        {
            var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.NamespaceOrType is { } target && RightmostName(target) == JobScope)
                .ToList();

            var names = usings.Where(u => u.Alias is not null)
                .Select(u => u.Alias!.Name.Identifier.ValueText)
                .Append(JobScope)
                .ToHashSet(StringComparer.Ordinal);

            return new FileNames(names, usings.Any(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)));
        }

        public bool NamesJobScope(ExpressionSyntax expression) =>
            RightmostName(expression) is { } name && JobScopeNames.Contains(name);

        private static string? RightmostName(SyntaxNode node) => node switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => null,
        };
    }

    private static List<string> Scan()
    {
        var root = RepositoryRoot();
        var sources = new List<(string File, SyntaxTree Tree)>();

        foreach (var tree in ScannedTrees)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, tree), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var file = Path.GetRelativePath(root, path).Replace('\\', '/');
                sources.Add((file, CSharpSyntaxTree.ParseText(File.ReadAllText(path))));
            }
        }

        var helpers = ScopeHelpers(sources.Where(s => s.File != Seam).Select(s => s.Tree).ToList());

        return sources
            .SelectMany(s => OpeningMembers(s.Tree, helpers).Select(member => $"{s.File}: {member}"))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }
}
