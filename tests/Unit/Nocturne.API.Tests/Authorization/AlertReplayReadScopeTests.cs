using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the read side of the alert replay surface. Both actions are POSTs that persist nothing,
/// so the write sweep exempts them, but the response carries the tenant's glucose, treatment and
/// device history as fact timelines over a caller-chosen window.
/// </summary>
[Trait("Category", "Unit")]
public class AlertReplayReadScopeTests
{
    private static AlertReplayResult OneTimelinePerCategory() =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch.AddHours(24), [])
        {
            FactTimelines = new Dictionary<string, IReadOnlyList<FactSnapshotPoint>>(StringComparer.Ordinal)
            {
                ["latest_glucose"] = [new FactSnapshotPoint(1, 42m)],
                ["iob"] = [new FactSnapshotPoint(1, 1.5m)],
                ["reservoir"] = [new FactSnapshotPoint(1, 80m)],
            },
        };

    private static IReadOnlySet<string> Granted(params string[] scopes) =>
        new HashSet<string>(scopes, StringComparer.Ordinal);

    /// <summary>The maximum a guest link can hold that is not the alerts category.</summary>
    private static string[] GuestScopesWithoutAlerts() =>
        Scope.Normalize([Scope.HealthRead, Scope.TherapyRead, Scope.ReportsRead]).ToArray();

    private static string[] ViewerScopes() =>
        Scope.NormalizeMemberPermissions(
            RoleSeeds.Permissions[RoleSeeds.Viewer]).ToArray();

    // ── admission ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(AlertReplayController.Replay))]
    [InlineData(nameof(AlertReplayController.ReplayDryRun))]
    public void ViewerRole_IsRefused(string action)
    {
        ViewerScopes().Should().NotContain(Scope.AlertsRead);

        Evaluate(action, authenticated: true, ViewerScopes())
            .Should().BeOfType<ForbidResult>("a Viewer holds no alerts scope");
    }

    [Theory]
    [InlineData(nameof(AlertReplayController.Replay))]
    [InlineData(nameof(AlertReplayController.ReplayDryRun))]
    public void GuestSessionWithoutTheAlertsCategory_IsRefused(string action)
    {
        Evaluate(action, authenticated: true, GuestScopesWithoutAlerts())
            .Should().BeOfType<ForbidResult>();
    }

    /// <summary>
    /// A public share is anonymous, and <see cref="Scope.AlertsRead"/> is outside
    /// <see cref="Scope.PublicShareScopes"/>, so no share can hold it however the
    /// owner configures the link. Requiring the single alerts scope is therefore not the
    /// share-breaking <c>requireAll</c> shape — a share was never admitted here to begin with.
    /// </summary>
    [Theory]
    [InlineData(nameof(AlertReplayController.Replay))]
    [InlineData(nameof(AlertReplayController.ReplayDryRun))]
    public void AnonymousShareLink_IsRefused(string action)
    {
        Scope.PublicShareScopes.Should().NotContain(Scope.AlertsRead);

        Evaluate(action, authenticated: false, [.. Scope.PublicShareScopes])
            .Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(nameof(AlertReplayController.Replay))]
    [InlineData(nameof(AlertReplayController.ReplayDryRun))]
    public void AlertsGrant_IsAdmitted(string action)
    {
        Evaluate(action, authenticated: true, [Scope.AlertsRead]).Should().BeNull();
        Evaluate(action, authenticated: true, [Scope.AlertsReadWrite]).Should().BeNull();
        Evaluate(action, authenticated: true, [Scope.FullAccess]).Should().BeNull();
    }

    /// <summary>
    /// Every seed role whose job includes authoring or reviewing alert rules must keep reaching the
    /// simulator, or the gate has broken the surface it protects.
    /// </summary>
    [Theory]
    [InlineData(RoleSeeds.Owner)]
    [InlineData(RoleSeeds.Admin)]
    [InlineData(RoleSeeds.Caretaker)]
    [InlineData(RoleSeeds.Clinician)]
    public void AlertHoldingSeedRoles_KeepReachingReplay(string role)
    {
        var scopes = Scope.NormalizeMemberPermissions(
            RoleSeeds.Permissions[role]).ToArray();

        Evaluate(nameof(AlertReplayController.Replay), authenticated: true, scopes).Should().BeNull();
    }

    // ── per-category redaction ────────────────────────────────────────────────────────────────

    [Fact]
    public void Redact_AlertsOnlyGrant_DropsEveryFactTimeline()
    {
        var result = AlertReplayReadScopeGuard.Redact(
            OneTimelinePerCategory(), Granted(Scope.AlertsRead));

        result.FactTimelines.Should().BeEmpty();
    }

    [Fact]
    public void Redact_GlucoseGrant_KeepsOnlyTheGlucoseFacts()
    {
        var result = AlertReplayReadScopeGuard.Redact(
            OneTimelinePerCategory(), Granted(Scope.AlertsRead, Scope.GlucoseRead));

        result.FactTimelines.Keys.Should().BeEquivalentTo(["latest_glucose"]);
    }

    [Fact]
    public void Redact_ReadWriteGrant_SatisfiesTheReadCategory()
    {
        var result = AlertReplayReadScopeGuard.Redact(
            OneTimelinePerCategory(), Granted(Scope.TreatmentsReadWrite));

        result.FactTimelines.Keys.Should().BeEquivalentTo(["iob"]);
    }

    [Fact]
    public void Redact_FullAccess_KeepsEveryFactTimeline()
    {
        var result = AlertReplayReadScopeGuard.Redact(
            OneTimelinePerCategory(), Granted(Scope.FullAccess));

        result.FactTimelines.Should().HaveCount(3);
    }

    [Fact]
    public void Redact_UnknownFactKey_IsDropped()
    {
        var withStray = OneTimelinePerCategory() with
        {
            FactTimelines = new Dictionary<string, IReadOnlyList<FactSnapshotPoint>>(StringComparer.Ordinal)
            {
                ["not_a_declared_fact"] = [new FactSnapshotPoint(1, 1m)],
            },
        };

        AlertReplayReadScopeGuard.Redact(withStray, Granted(Scope.FullAccess))
            .FactTimelines.Should().BeEmpty("a fact with no declared category must fail closed");
    }

    /// <summary>
    /// The categories are declared on the facts themselves, so a new fact cannot reach the wire
    /// under a scope the taxonomy does not know or that no share category governs.
    /// </summary>
    [Fact]
    public void EveryReplayFact_DeclaresAGovernedReadScope()
    {
        var facts = typeof(SensorContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<ReplayFactAttribute>(inherit: false))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        facts.Should().HaveCountGreaterThan(10, "the scan must find the fact surface");

        foreach (var fact in facts)
        {
            ShareDataCategories.GoverningScopes.Should().Contain(fact.Scope,
                $"replay fact '{fact.Key}' declares '{fact.Scope}', which governs no data category");
        }
    }

    // ── handler wiring ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(AlertReplayController.Replay))]
    [InlineData(nameof(AlertReplayController.ReplayDryRun))]
    public async Task Handler_RedactsTheCategoriesTheCallerLacks(string action)
    {
        var service = new Mock<IAlertReplayService>();
        service
            .Setup(s => s.ReplayAsync(
                It.IsAny<DateOnly?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneTimelinePerCategory());
        service
            .Setup(s => s.ReplayDryRunAsync(
                It.IsAny<DateOnly?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<ReplayRuleOverride>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneTimelinePerCategory());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Granted(Scope.AlertsRead, Scope.DevicesRead);

        var controller = new AlertReplayController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var response = action == nameof(AlertReplayController.Replay)
            ? await controller.Replay(new AlertReplayRequest(null, null), CancellationToken.None)
            : await controller.ReplayDryRun(
                new AlertReplayDryRunRequest(null, null, NewRuleDefinition()), CancellationToken.None);

        var result = response.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AlertReplayResult>().Subject;

        result.FactTimelines.Keys.Should().BeEquivalentTo(["reservoir"]);
    }

    private static ReplayRuleDefinition NewRuleDefinition() => new(
        Id: null,
        Name: "rule",
        ConditionType: AlertConditionType.Threshold,
        ConditionParams: "{}",
        Severity: AlertRuleSeverity.Warning,
        AllowThroughDnd: false,
        AutoResolveEnabled: false,
        AutoResolveParams: null);

    /// <summary>
    /// Runs the authorization filters the action really answers to — MVC applies class-level
    /// filters alongside the action's own, and the gate here is declared on the class.
    /// </summary>
    private static IActionResult? Evaluate(string actionName, bool authenticated, string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
            httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes, StringComparer.Ordinal);

        var action = typeof(AlertReplayController)
            .GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!;

        var filters = typeof(AlertReplayController).GetCustomAttributes(inherit: true)
            .Concat(action.GetCustomAttributes(inherit: true))
            .OfType<IAuthorizationFilter>();

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        foreach (var filter in filters)
        {
            var context = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
            filter.OnAuthorization(context);
            if (context.Result is not null)
                return context.Result;
        }

        return null;
    }
}
