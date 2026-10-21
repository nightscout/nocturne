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
/// caller's <see cref="TenantReader"/>; that is not checked here. Every other scope, raw
/// (<c>CreateScope</c>, <c>CreateAsyncScope</c>) or from
/// <see cref="TenantJobScope.Open(IServiceProvider, TenantContext?, string)"/>, is limited to the
/// members listed here, each for the reason its group gives. An entry names the file and the member
/// the call sits in, so a new method in a listed file is not covered by its neighbour.
/// </summary>
[Trait("Category", "Unit")]
public class ChildScopeGuardTests
{
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
        "src/API/Nocturne.API/Services/Alerts/RuleScopeClassBackfillService.cs: RuleScopeClassBackfillService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Auth/AuthorizationSeedService.cs: AuthorizationSeedService.StartAsync",
        "src/API/Nocturne.API/Services/Auth/OAuthCodeCleanupService.cs: OAuthCodeCleanupService.CleanupExpiredCodesAsync",
        "src/API/Nocturne.API/Services/Auth/OidcProviderService.cs: OidcProviderService.SyncConfigProvidersAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/ApsSnapshotCurvesBackfillService.cs: ApsSnapshotCurvesBackfillService.ExecuteAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/CompressionLowDetectionService.cs: CompressionLowDetectionService.ComputeNextRunDelayAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/CompressionLowDetectionService.cs: CompressionLowDetectionService.RunForAllTenantsAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/ConnectorBackgroundService.cs: ConnectorBackgroundService.SyncForTenantAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/DeduplicationReconciliationBackgroundService.cs: DeduplicationReconciliationBackgroundService.ReconcileAllTenantsAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/DemoServiceHealthMonitor.cs: DemoServiceHealthMonitor.CleanupDemoDataAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/NightscoutConnectorBackgroundService.cs: NightscoutConnectorBackgroundService.StartListenerForTenantAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/NocturneRemoteConnectorBackgroundService.cs: NocturneRemoteConnectorBackgroundService.StartRealtimeListenersAsync",
        "src/API/Nocturne.API/Services/BackgroundServices/NotificationCleanupService.cs: NotificationCleanupService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Chat/ChatIdentityPendingLinkCleanupService.cs: ChatIdentityPendingLinkCleanupService.SweepAsync",
        "src/API/Nocturne.API/Services/Compatibility/DiscrepancyMaintenanceService.cs: DiscrepancyMaintenanceService.PerformMaintenanceAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthWorker.cs: GoogleHealthWorker.ProcessRequestsAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/GoogleHealthWorker.cs: GoogleHealthWorker.SyncTenantAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationStartupService.cs: MigrationStartupService.StartAsync",
        "src/API/Nocturne.API/Services/Monitoring/TrackerAlertRuleBackfillService.cs: TrackerAlertRuleBackfillService.ExecuteAsync",
        "src/API/Nocturne.API/Services/Notifications/NotificationResolutionService.cs: NotificationResolutionService.EvaluateAllTenantsAsync",

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
        "src/API/Nocturne.API/Services/Health/GoogleHealth/PostgresGoogleHealthCoordinatorStore.cs: PostgresGoogleHealthCoordinatorStore.LockAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/PostgresGoogleHealthCoordinatorStore.cs: PostgresGoogleHealthCoordinatorStore.OpenRequestListenerAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/PostgresGoogleHealthCoordinatorStore.cs: PostgresGoogleHealthCoordinatorStore.ReadRequestsAsync",
        "src/API/Nocturne.API/Services/Health/GoogleHealth/PostgresGoogleHealthCoordinatorStore.cs: PostgresGoogleHealthCoordinatorStore.TenantScope",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJob.PersistSnapshotAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.FindRunAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.GetHistoryAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.GetSourcesAsync",
        "src/API/Nocturne.API/Services/Migration/MigrationJobService.cs: MigrationJobService.TestApiConnectionAsync",

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
        "src/API/Nocturne.API/Services/Seeding/SampleDataSeeder.cs: SampleDataSeeder.SeedInTenantScopeAsync",
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
    [InlineData("class C { void M() => Use(p.CreateScope); }", "C.M")]
    [InlineData("class C { void M() { Task.Run(async () => { await using var s = f.CreateAsyncScope(); }); } }", "C.M")]
    [InlineData("class O { class C { IServiceScope P => p.CreateScope(); } }", "O.C.P")]
    [InlineData("class C { void M() { var s = TenantJobScope.OpenFor(p, r, \"a\"); } }", null)]
    [InlineData("class C { void M() { var s = File.Open(path); } }", null)]
    public void FindsTheOpeningMember(string source, string? expected)
    {
        var members = OpeningMembers(CSharpSyntaxTree.ParseText(source));

        if (expected is null)
            members.Should().BeEmpty();
        else
            members.Should().Equal(expected);
    }

    /// <summary>
    /// The member around every mention by name of a raw scope method (a call, a conditional call or a
    /// method group) and around every <c>TenantJobScope.Open</c>.
    /// </summary>
    private static List<string> OpeningMembers(SyntaxTree syntax) =>
        syntax.GetRoot()
            .DescendantNodes()
            .Where(node => node switch
            {
                MemberAccessExpressionSyntax member =>
                    RawScopeMethods.Contains(member.Name.Identifier.ValueText)
                    || (member.Name.Identifier.ValueText == nameof(TenantJobScope.Open)
                        && member.Expression is IdentifierNameSyntax { Identifier.ValueText: nameof(TenantJobScope) }),
                MemberBindingExpressionSyntax binding => RawScopeMethods.Contains(binding.Name.Identifier.ValueText),
                _ => false,
            })
            .Select(EnclosingMember.Of)
            .Distinct()
            .ToList();

    private static List<string> Scan()
    {
        var root = RepositoryRoot();
        var openers = new List<string>();

        foreach (var tree in ScannedTrees)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, tree), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var text = File.ReadAllText(path);
                if (!RawScopeMethods.Any(m => text.Contains(m, StringComparison.Ordinal))
                    && !text.Contains(nameof(TenantJobScope), StringComparison.Ordinal))
                    continue;

                var file = Path.GetRelativePath(root, path).Replace('\\', '/');
                openers.AddRange(OpeningMembers(CSharpSyntaxTree.ParseText(text))
                    .Select(member => $"{file}: {member}"));
            }
        }

        return openers;
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
