using FluentAssertions;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

[Trait("Category", "Unit")]
public class ActivityWriteScopeGuardTests
{
    private static Activity Activity(string id) => new() { Id = id, Mills = 1700000000000 };

    /// <summary>
    /// Stub decomposer that maps activity ids to a canned required scope, so the guard's
    /// scope-checking logic can be exercised without a real classifier or DbContext.
    /// </summary>
    private static IActivityDecomposer Decomposer(Dictionary<string, string?> idToScope)
    {
        var mock = new Mock<IActivityDecomposer>();
        mock.Setup(d => d.RequiredWriteScope(It.IsAny<Activity>()))
            .Returns((Activity a) => idToScope.GetValueOrDefault(a.Id!));
        return mock.Object;
    }

    private static IReadOnlySet<string> Granted(params string[] scopes) =>
        new HashSet<string>(scopes);

    [Fact]
    public void FindMissingScope_AllRegular_ReturnsNull()
    {
        var decomposer = Decomposer(new() { ["a"] = null, ["b"] = null });

        var missing = ActivityWriteScopeGuard.FindMissingScope(
            [Activity("a"), Activity("b")], decomposer, Granted());

        missing.Should().BeNull();
    }

    [Fact]
    public void FindMissingScope_SleepRecordWithoutScope_ReturnsSleepScope()
    {
        var decomposer = Decomposer(new() { ["s"] = Scope.SleepReadWrite });

        var missing = ActivityWriteScopeGuard.FindMissingScope(
            [Activity("s")], decomposer, Granted(Scope.GlucoseReadWrite));

        missing.Should().Be(Scope.SleepReadWrite);
    }

    [Fact]
    public void FindMissingScope_SleepRecordWithScope_ReturnsNull()
    {
        var decomposer = Decomposer(new() { ["s"] = Scope.SleepReadWrite });

        var missing = ActivityWriteScopeGuard.FindMissingScope(
            [Activity("s")], decomposer, Granted(Scope.SleepReadWrite));

        missing.Should().BeNull();
    }

    [Fact]
    public void FindMissingScope_FullAccessSatisfiesEveryCategory()
    {
        var decomposer = Decomposer(new()
        {
            ["hr"] = Scope.HeartRateReadWrite,
            ["sc"] = Scope.StepCountReadWrite,
            ["s"] = Scope.SleepReadWrite,
        });

        var missing = ActivityWriteScopeGuard.FindMissingScope(
            [Activity("hr"), Activity("sc"), Activity("s")], decomposer, Granted(Scope.FullAccess));

        missing.Should().BeNull();
    }

    [Fact]
    public void FindMissingScope_MixedBatch_ReturnsTheMissingCategory()
    {
        // Caller can write steps but not sleep; the sleep record in the batch is the gap.
        var decomposer = Decomposer(new()
        {
            ["sc"] = Scope.StepCountReadWrite,
            ["s"] = Scope.SleepReadWrite,
        });

        var missing = ActivityWriteScopeGuard.FindMissingScope(
            [Activity("sc"), Activity("s")], decomposer, Granted(Scope.StepCountReadWrite));

        missing.Should().Be(Scope.SleepReadWrite);
    }
}
