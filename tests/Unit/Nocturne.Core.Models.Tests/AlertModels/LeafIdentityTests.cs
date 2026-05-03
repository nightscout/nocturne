using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.AlertModels;

/// <summary>
/// Pre-order DFS leaf-id assignment over the recursive <see cref="ConditionNode"/> tree.
/// Container nodes (composite/not/sustained) are unwrapped — only their leaves get IDs.
/// </summary>
[Trait("Category", "Unit")]
public class LeafIdentityTests
{
    private static ConditionNode Threshold(string direction, decimal value) =>
        new("threshold", Threshold: new ThresholdCondition(direction, value));

    [Fact]
    public void SingleThresholdLeaf_GetsIdZero()
    {
        var leaf = Threshold("below", 70m);

        var ids = LeafIdentity.AssignLeafIds(leaf);

        ids.Should().HaveCount(1);
        ids[0].LeafId.Should().Be(0);
        ids[0].Node.Should().BeSameAs(leaf);
    }

    [Fact]
    public void CompositeAndOfTwoLeaves_AssignsZeroThenOne()
    {
        var a = Threshold("below", 70m);
        var b = Threshold("above", 180m);
        var root = new ConditionNode("composite",
            Composite: new CompositeCondition("and", [a, b]));

        var ids = LeafIdentity.AssignLeafIds(root);

        ids.Should().HaveCount(2);
        ids[0].LeafId.Should().Be(0);
        ids[0].Node.Should().BeSameAs(a);
        ids[1].LeafId.Should().Be(1);
        ids[1].Node.Should().BeSameAs(b);
    }

    [Fact]
    public void NotWrappedLeafInsideAnd_RecordsInnerLeafNotWrapper()
    {
        var a = Threshold("below", 70m);
        var inner = Threshold("above", 180m);
        var notNode = new ConditionNode("not", Not: new NotCondition(inner));
        var root = new ConditionNode("composite",
            Composite: new CompositeCondition("and", [a, notNode]));

        var ids = LeafIdentity.AssignLeafIds(root);

        ids.Should().HaveCount(2);
        ids[0].LeafId.Should().Be(0);
        ids[0].Node.Should().BeSameAs(a);
        ids[1].LeafId.Should().Be(1);
        // Inner leaf — not the NOT wrapper — is what's recorded.
        ids[1].Node.Should().BeSameAs(inner);
    }

    [Fact]
    public void SustainedWrappedLeaf_RecordsInnerLeafNotSustainedNode()
    {
        var inner = Threshold("below", 70m);
        var sustained = new ConditionNode("sustained",
            Sustained: new SustainedCondition(15, inner));

        var ids = LeafIdentity.AssignLeafIds(sustained);

        ids.Should().HaveCount(1);
        ids[0].LeafId.Should().Be(0);
        ids[0].Node.Should().BeSameAs(inner);
    }

    [Fact]
    public void Phase2LeafType_TreatedAsLeafByDefault()
    {
        // Locks in the "default = leaf" guarantee: any non-container condition
        // type (including the Phase 2 additions like pump_state, glucose_bucket,
        // time_since_last_carb, etc.) gets a leaf id without per-type plumbing.
        var pumpState = new ConditionNode("pump_state",
            PumpState: new PumpStateCondition(PumpModeState.Manual, IsActive: true, ForMinutes: null));

        var ids = LeafIdentity.AssignLeafIds(pumpState);

        ids.Should().HaveCount(1);
        ids[0].LeafId.Should().Be(0);
        ids[0].Node.Should().BeSameAs(pumpState);
    }

    [Fact]
    public void NestedComposite_PreOrderAcrossNesting()
    {
        // AND(a, OR(b, c)) → [a:0, b:1, c:2]
        var a = Threshold("below", 70m);
        var b = Threshold("above", 180m);
        var c = Threshold("above", 250m);
        var inner = new ConditionNode("composite",
            Composite: new CompositeCondition("or", [b, c]));
        var root = new ConditionNode("composite",
            Composite: new CompositeCondition("and", [a, inner]));

        var ids = LeafIdentity.AssignLeafIds(root);

        ids.Should().HaveCount(3);
        ids[0].LeafId.Should().Be(0);
        ids[0].Node.Should().BeSameAs(a);
        ids[1].LeafId.Should().Be(1);
        ids[1].Node.Should().BeSameAs(b);
        ids[2].LeafId.Should().Be(2);
        ids[2].Node.Should().BeSameAs(c);
    }
}
