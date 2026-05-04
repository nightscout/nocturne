using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ComparisonOpsTests
{
    [Theory]
    [InlineData(5, "<", 10, true)]
    [InlineData(10, "<", 10, false)]
    [InlineData(15, "<", 10, false)]
    [InlineData(5, "<=", 10, true)]
    [InlineData(10, "<=", 10, true)]
    [InlineData(15, "<=", 10, false)]
    [InlineData(15, ">", 10, true)]
    [InlineData(10, ">", 10, false)]
    [InlineData(5, ">", 10, false)]
    [InlineData(15, ">=", 10, true)]
    [InlineData(10, ">=", 10, true)]
    [InlineData(5, ">=", 10, false)]
    [InlineData(10, "==", 10, true)]
    [InlineData(11, "==", 10, false)]
    public void Compare_AppliesOperator(decimal actual, string op, decimal threshold, bool expected)
    {
        ComparisonOps.Compare(actual, op, threshold).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!=")]
    [InlineData("=<")]
    [InlineData("=")]
    [InlineData("LT")]
    public void Compare_UnknownOperator_ReturnsFalse(string op)
    {
        ComparisonOps.Compare(5, op, 10).Should().BeFalse();
        ComparisonOps.Compare(10, op, 10).Should().BeFalse();
        ComparisonOps.Compare(15, op, 10).Should().BeFalse();
    }
}
