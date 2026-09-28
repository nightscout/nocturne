using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ConditionTreeEqualityTests
{
    [Theory]
    [InlineData("""{"a":1,"b":[true,null,"x"]}""", """ { "b" : [ true , null , "x" ] , "a" : 1 } """)]
    [InlineData(null, "  ")]
    [InlineData("""{"direction":"below"}""", """{"direction":"below"}""")]
    public void Formatting_and_key_order_do_not_matter(string? stored, string? requested) =>
        ConditionTreeEquality.Same(stored, requested).Should().BeTrue();

    [Theory]
    [InlineData("""{"a":1,"a":2}""", """{"a":1,"a":2}""")]
    [InlineData("""{"a":1,"a":2}""", """{"a":2}""")]
    [InlineData("""{"Direction":"below"}""", """{"direction":"below"}""")]
    [InlineData("""{"a":1,"A":2}""", """{"a":1,"A":2}""")]
    [InlineData("{", "{")]
    public void Names_bind_as_the_engines_bind_them_and_identical_text_is_the_same_tree(string stored, string requested) =>
        ConditionTreeEquality.Same(stored, requested).Should().BeTrue();

    [Theory]
    [InlineData("""{"minutes":1.0}""", """{"minutes":1}""")]
    [InlineData("""{"minutes":1e0}""", """{"minutes":1}""")]
    [InlineData("""{"a":[1,2]}""", """{"a":[2,1]}""")]
    [InlineData("""{"value":-0}""", """{"value":0}""")]
    [InlineData("""{"value":"70"}""", """{"value":70}""")]
    [InlineData("""{"direction":"Below"}""", """{"direction":"below"}""")]
    [InlineData("{", "{ ")]
    [InlineData("""{"a":1}""", null)]
    [InlineData("""{"a":2,"a":1}""", """{"a":2}""")]
    [InlineData("""{"a":2,"A":1}""", """{"a":2}""")]
    [InlineData("""{"a":1,"A":2}""", """{"a":2}""")]
    [InlineData("""{"a":1,"A":2}""", """{"A":2,"a":1}""")]
    public void Anything_else_differs(string? stored, string? requested) =>
        ConditionTreeEquality.Same(stored, requested).Should().BeFalse();
}
