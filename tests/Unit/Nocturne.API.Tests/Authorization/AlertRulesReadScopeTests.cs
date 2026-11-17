using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the read side of the alert rules surface. A rule names its delivery channels, so the
/// phone numbers and webhook targets a rule holds must not reach a credential that was never
/// granted the alerts category.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesReadScopeTests
{
    private static string[] SeedRoleScopes(string role) =>
        Scope.NormalizeMemberPermissions(RoleSeeds.Permissions[role]).ToArray();

    [Theory]
    [InlineData(nameof(AlertRulesController.GetRules))]
    [InlineData(nameof(AlertRulesController.GetRule))]
    public void CredentialWithoutAlertsRead_IsRefused(string action)
    {
        Evaluate(action, authenticated: true, [Scope.GlucoseRead]).Should().BeOfType<ForbidResult>();
        Evaluate(action, authenticated: true, SeedRoleScopes(RoleSeeds.Viewer)).Should().BeOfType<ForbidResult>();
        Evaluate(action, authenticated: false, [.. Scope.PublicShareScopes]).Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(nameof(AlertRulesController.GetRules))]
    [InlineData(nameof(AlertRulesController.GetRule))]
    public void AlertsGrant_IsAdmitted(string action)
    {
        Evaluate(action, authenticated: true, [Scope.AlertsRead]).Should().BeNull();
        Evaluate(action, authenticated: true, [Scope.AlertsReadWrite]).Should().BeNull();
        Evaluate(action, authenticated: true, [Scope.FullAccess]).Should().BeNull();
    }

    [Theory]
    [InlineData(RoleSeeds.Owner)]
    [InlineData(RoleSeeds.Admin)]
    [InlineData(RoleSeeds.Caretaker)]
    [InlineData(RoleSeeds.Clinician)]
    public void AlertHoldingSeedRoles_KeepReadingRules(string role)
    {
        Evaluate(nameof(AlertRulesController.GetRules), authenticated: true, SeedRoleScopes(role))
            .Should().BeNull();
    }

    [Fact]
    public void DemoVisitor_KeepsReadingRules()
    {
        var scopes = Scope.NormalizeMemberPermissions(Scope.DemoVisitorPermissions).ToArray();

        Evaluate(nameof(AlertRulesController.GetRules), authenticated: true, scopes).Should().BeNull();
    }

    /// <summary>
    /// A guest or share link holding alerts.read sees a firing alarm, not the rules behind it: a rule
    /// names its delivery channels. <see cref="Nocturne.API.Authorization.DenyNoAccountViewerAttribute"/>.
    /// </summary>
    [Theory]
    [InlineData(nameof(AlertRulesController.GetRules))]
    [InlineData(nameof(AlertRulesController.GetRule))]
    public void NoAccountViewerHoldingAlertsRead_IsRefused(string action)
    {
        var scopes = new HashSet<string>(Scope.Normalize([Scope.AlertsRead]), StringComparer.Ordinal);

        ActionGate.Evaluate(typeof(AlertRulesController), action, scopes,
                new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest })
            .Should().BeOfType<ForbidResult>();
        ActionGate.Evaluate(typeof(AlertRulesController), action, scopes,
                new AuthContext { IsAuthenticated = false })
            .Should().BeOfType<ForbidResult>();
    }

    private static IActionResult? Evaluate(string actionName, bool authenticated, string[] grantedScopes) =>
        ActionGate.Evaluate(
            typeof(AlertRulesController),
            actionName,
            new HashSet<string>(grantedScopes, StringComparer.Ordinal),
            new AuthContext { IsAuthenticated = authenticated });
}
