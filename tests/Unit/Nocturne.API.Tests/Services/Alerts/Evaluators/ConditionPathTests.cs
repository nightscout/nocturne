using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ConditionPathTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Build_RootOnly_ReturnsKindWithoutIndex()
    {
        ConditionPath.Build(new[] { ("composite", ConditionPath.RootIndex) })
            .Should().Be("composite");
    }

    [Fact]
    public void Build_RootAndChild_JoinsWithBracketIndex()
    {
        ConditionPath.Build(new[]
        {
            ("composite", ConditionPath.RootIndex),
            ("sustained", 0)
        }).Should().Be("composite[0].sustained");
    }

    [Fact]
    public void Build_DeepPath_StacksIndexedSegments()
    {
        ConditionPath.Build(new[]
        {
            ("composite", ConditionPath.RootIndex),
            ("sustained", 0),
            ("threshold", 0)
        }).Should().Be("composite[0].sustained[0].threshold");
    }

    [Fact]
    public void Build_EmptySegments_ReturnsEmptyString()
    {
        ConditionPath.Build(Array.Empty<(string, int)>()).Should().BeEmpty();
    }

    [Fact]
    public void Walk_SingleNodeRoot_VisitsOnceWithKind()
    {
        var node = new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70));
        var visited = new List<string>();
        var result = Walker.Collect(node, visited);

        result.Should().BeNull();
        visited.Should().ContainSingle().Which.Should().Be("threshold");
    }

    [Fact]
    public void Walk_CompositeWithMultipleChildren_VisitsInDocumentOrder()
    {
        var root = new ConditionNode(
            "composite",
            Composite: new CompositeCondition("and", new List<ConditionNode>
            {
                new("threshold", Threshold: new ThresholdCondition("below", 70)),
                new("sustained", Sustained: new SustainedCondition(15,
                    new ConditionNode("threshold", Threshold: new ThresholdCondition("above", 180)))),
                new("rate_of_change", RateOfChange: new RateOfChangeCondition("falling", 2))
            }));

        var visited = new List<string>();
        Walker.Collect(root, visited);

        visited.Should().Equal(
            "composite",
            "composite[0].threshold",
            "composite[1].sustained",
            "composite[1].sustained[0].threshold",
            "composite[2].rate_of_change");
    }

    [Fact]
    public void Walk_NotWrapper_DescendsIntoChildAtIndexZero()
    {
        var root = new ConditionNode(
            "not",
            Not: new NotCondition(new ConditionNode("threshold",
                Threshold: new ThresholdCondition("above", 200))));

        var visited = new List<string>();
        Walker.Collect(root, visited);

        visited.Should().Equal("not", "not[0].threshold");
    }

    [Fact]
    public void Walk_ReturnsFirstNonNullAndShortCircuits()
    {
        var root = BuildSampleTree();
        var visitCount = 0;

        var hit = ConditionPath.Walk<string>(root, (node, path) =>
        {
            visitCount++;
            return node.Type == "sustained" ? path : null;
        });

        hit.Should().Be("composite[0].sustained");
        // composite (visit 1) -> composite[0].sustained (visit 2) -> stop.
        visitCount.Should().Be(2);
    }

    [Fact]
    public void Walk_NoMatch_ReturnsNull()
    {
        var root = BuildSampleTree();

        var hit = ConditionPath.Walk<string>(root, (_, _) => null);

        hit.Should().BeNull();
    }

    [Fact]
    public void Walk_PathIsStableAcrossReSerialisation()
    {
        var original = BuildSampleTree();
        var beforePaths = new List<string>();
        Walker.Collect(original, beforePaths);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<ConditionNode>(json, JsonOptions)!;
        var afterPaths = new List<string>();
        Walker.Collect(roundTripped, afterPaths);

        afterPaths.Should().Equal(beforePaths);
        afterPaths.Should().Contain("composite[0].sustained");
    }

    [Fact]
    public void Walk_PathFromBuildMatchesPathFromWalk()
    {
        var root = BuildSampleTree();

        var built = ConditionPath.Build(new[]
        {
            ("composite", ConditionPath.RootIndex),
            ("sustained", 0),
            ("threshold", 0)
        });

        var walked = ConditionPath.Walk<string>(root, (node, path) =>
            node.Type == "threshold" ? path : null);

        walked.Should().Be(built);
    }

    private static ConditionNode BuildSampleTree() => new(
        "composite",
        Composite: new CompositeCondition("and", new List<ConditionNode>
        {
            new("sustained", Sustained: new SustainedCondition(10,
                new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)))),
            new("not", Not: new NotCondition(
                new ConditionNode("threshold", Threshold: new ThresholdCondition("above", 250))))
        }));

    private static class Walker
    {
        // Returns null so the walker visits every node; the side effect is the captured paths.
        public static string? Collect(ConditionNode root, List<string> paths) =>
            ConditionPath.Walk<string>(root, (_, path) =>
            {
                paths.Add(path);
                return null;
            });
    }
}
