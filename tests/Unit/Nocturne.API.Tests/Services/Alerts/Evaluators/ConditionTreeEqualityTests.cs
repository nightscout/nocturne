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
    public void Formatting_and_key_order_do_not_matter(string? stored, string? requested) =>
        ConditionTreeEquality.Same(stored, requested).Should().BeTrue();

    [Theory]
    [InlineData("""{"minutes":1.0}""", """{"minutes":1}""")]
    [InlineData("""{"minutes":1e0}""", """{"minutes":1}""")]
    [InlineData("""{"a":1,"a":2}""", """{"a":1,"a":2}""")]
    [InlineData("""{"a":[1,2]}""", """{"a":[2,1]}""")]
    [InlineData("{", "{ ")]
    [InlineData("""{"a":1}""", null)]
    public void Anything_else_differs(string? stored, string? requested) =>
        ConditionTreeEquality.Same(stored, requested).Should().BeFalse();
}
