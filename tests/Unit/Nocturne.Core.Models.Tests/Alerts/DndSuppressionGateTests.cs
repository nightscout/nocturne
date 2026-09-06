using System.Collections.Generic;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.Alerts;

/// <summary>
/// Server mirror of the device's <c>DndSuppressionTest</c> (Prelude
/// <c>core/model/.../alerts/DndSuppressionTest.kt</c>): the scoped Do Not Disturb gate
/// (ADR 0004 D5) must agree with the device byte-for-byte — scope/class coverage,
/// Critical + AllowThroughDnd piercing, and union-over-active-scopes.
/// </summary>
[Trait("Category", "Unit")]
public class DndSuppressionGateTests
{
    private static IReadOnlySet<DndScope> Scopes(params DndScope[] scopes) => new HashSet<DndScope>(scopes);

    private static AlertRuleSnapshot Rule(
        RuleScopeClass scopeClass = RuleScopeClass.Low,
        AlertRuleSeverity severity = AlertRuleSeverity.Warning,
        bool allowThroughDnd = false) =>
        new(
            Id: System.Guid.NewGuid(),
            TenantId: System.Guid.NewGuid(),
            Name: "r",
            ConditionType: AlertConditionType.Threshold,
            ConditionParams: "{}",
            Severity: severity,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: false,
            AutoResolveParams: null,
            AllowThroughDnd: allowThroughDnd,
            ScopeClass: scopeClass);

    // ---- scope/class coverage ----

    [Fact]
    public void Covers_mirrors_the_device_coveredBy()
    {
        // all covers everything
        foreach (var cls in new[] { RuleScopeClass.Low, RuleScopeClass.High, RuleScopeClass.Composite, RuleScopeClass.Undirected })
            DndSuppressionGate.Covers(DndScope.All, cls).Should().BeTrue();

        DndSuppressionGate.Covers(DndScope.Lows, RuleScopeClass.Low).Should().BeTrue();
        DndSuppressionGate.Covers(DndScope.Lows, RuleScopeClass.High).Should().BeFalse();
        DndSuppressionGate.Covers(DndScope.Highs, RuleScopeClass.High).Should().BeTrue();
        DndSuppressionGate.Covers(DndScope.Highs, RuleScopeClass.Low).Should().BeFalse();

        // composite/undirected are all-only
        DndSuppressionGate.Covers(DndScope.Lows, RuleScopeClass.Composite).Should().BeFalse();
        DndSuppressionGate.Covers(DndScope.Highs, RuleScopeClass.Undirected).Should().BeFalse();
    }

    [Fact]
    public void Lows_window_suppresses_a_low_rule() =>
        DndSuppressionGate.IsSuppressed(Rule(RuleScopeClass.Low), Scopes(DndScope.Lows)).Should().BeTrue();

    [Fact]
    public void Lows_window_does_not_suppress_a_high_rule() =>
        DndSuppressionGate.IsSuppressed(Rule(RuleScopeClass.High), Scopes(DndScope.Lows)).Should().BeFalse();

    [Theory]
    [InlineData(RuleScopeClass.Low)]
    [InlineData(RuleScopeClass.High)]
    [InlineData(RuleScopeClass.Composite)]
    [InlineData(RuleScopeClass.Undirected)]
    public void All_window_suppresses_every_class(RuleScopeClass cls) =>
        DndSuppressionGate.IsSuppressed(Rule(cls), Scopes(DndScope.All)).Should().BeTrue();

    [Theory]
    [InlineData(RuleScopeClass.Composite)]
    [InlineData(RuleScopeClass.Undirected)]
    public void Composite_and_undirected_are_silenced_only_by_an_all_window(RuleScopeClass cls)
    {
        DndSuppressionGate.IsSuppressed(Rule(cls), Scopes(DndScope.Lows)).Should().BeFalse();
        DndSuppressionGate.IsSuppressed(Rule(cls), Scopes(DndScope.Highs)).Should().BeFalse();
        DndSuppressionGate.IsSuppressed(Rule(cls), Scopes(DndScope.All)).Should().BeTrue();
    }

    // ---- piercing ----

    [Fact]
    public void Critical_pierces_every_scope() =>
        DndSuppressionGate.IsSuppressed(
            Rule(RuleScopeClass.Low, severity: AlertRuleSeverity.Critical), Scopes(DndScope.All))
            .Should().BeFalse();

    [Fact]
    public void Allow_through_dnd_rule_is_never_suppressed() =>
        DndSuppressionGate.IsSuppressed(
            Rule(RuleScopeClass.Low, allowThroughDnd: true), Scopes(DndScope.All))
            .Should().BeFalse();

    [Fact]
    public void No_active_scopes_never_suppresses() =>
        DndSuppressionGate.IsSuppressed(Rule(RuleScopeClass.Low), Scopes()).Should().BeFalse();

    // ---- which scope is recorded (server-only: drives SuppressionReason "dnd:<scope>") ----

    [Fact]
    public void SuppressingScope_prefers_the_class_specific_scope_over_all()
    {
        // A low rule muted while both lows and all are active records the precise cause.
        DndSuppressionGate.SuppressingScope(Rule(RuleScopeClass.Low), Scopes(DndScope.Lows, DndScope.All))
            .Should().Be(DndScope.Lows);
    }

    [Fact]
    public void SuppressingScope_falls_back_to_all_for_an_all_only_class()
    {
        DndSuppressionGate.SuppressingScope(Rule(RuleScopeClass.Composite), Scopes(DndScope.All))
            .Should().Be(DndScope.All);
    }

    [Fact]
    public void SuppressingScope_is_null_when_not_covered() =>
        DndSuppressionGate.SuppressingScope(Rule(RuleScopeClass.High), Scopes(DndScope.Lows))
            .Should().BeNull();
}
