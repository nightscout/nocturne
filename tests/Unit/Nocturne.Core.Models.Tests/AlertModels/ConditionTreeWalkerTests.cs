using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.AlertModels;

/// <summary>
/// Recursive walker that detects a <c>state_span_active</c> leaf with
/// <see cref="StateSpanCategory.PumpMode"/> anywhere in the condition tree. Used by
/// <c>AlertRulesController</c> to reject these rules upfront — the runtime
/// <c>StateSpanActiveEvaluator</c> fails closed for that combination, so without an
/// upfront 400 the user would silently get a rule that never fires.
/// </summary>
[Trait("Category", "Unit")]
public class ConditionTreeWalkerTests
{
    private static ConditionNode StateSpan(StateSpanCategory category, string? state = null) =>
        new("state_span_active",
            StateSpanActive: new StateSpanActiveCondition(category, state, IsActive: true, ForMinutes: null));

    private static ConditionNode Threshold() =>
        new("threshold",
            Threshold: new ThresholdCondition(Direction: "high", Value: 180m));

    [Fact]
    public void TopLevelStateSpanActiveWithPumpMode_IsRejected()
    {
        var tree = StateSpan(StateSpanCategory.PumpMode);

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeTrue();
    }

    [Fact]
    public void TopLevelStateSpanActiveWithNonPumpMode_IsAccepted()
    {
        var tree = StateSpan(StateSpanCategory.Sleep);

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeFalse();
    }

    [Fact]
    public void CompositeContainingPumpModeStateSpan_IsRejected()
    {
        var tree = new ConditionNode("composite",
            Composite: new CompositeCondition("and", new List<ConditionNode>
            {
                StateSpan(StateSpanCategory.PumpMode),
                Threshold(),
            }));

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeTrue();
    }

    [Fact]
    public void NotContainingPumpModeStateSpan_IsRejected()
    {
        var tree = new ConditionNode("not",
            Not: new NotCondition(StateSpan(StateSpanCategory.PumpMode)));

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeTrue();
    }

    [Fact]
    public void SustainedContainingPumpModeStateSpan_IsRejected()
    {
        var tree = new ConditionNode("sustained",
            Sustained: new SustainedCondition(Minutes: 5, Child: StateSpan(StateSpanCategory.PumpMode)));

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeTrue();
    }

    [Fact]
    public void CompositeOfTwoThresholds_IsAccepted()
    {
        var tree = new ConditionNode("composite",
            Composite: new CompositeCondition("and", new List<ConditionNode>
            {
                Threshold(),
                Threshold(),
            }));

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeFalse();
    }

    [Fact]
    public void DeeplyNestedPumpModeStateSpan_IsRejected()
    {
        // composite -> sustained -> not -> state_span_active(PumpMode)
        var tree = new ConditionNode("composite",
            Composite: new CompositeCondition("or", new List<ConditionNode>
            {
                Threshold(),
                new ConditionNode("sustained",
                    Sustained: new SustainedCondition(10,
                        new ConditionNode("not",
                            Not: new NotCondition(StateSpan(StateSpanCategory.PumpMode))))),
            }));

        ConditionTreeWalker.ContainsPumpModeStateSpan(tree).Should().BeTrue();
    }
}
