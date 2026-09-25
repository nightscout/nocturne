using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins <see cref="Scope.ValidateDelegation"/>, the ceiling a caller passes when it delegates a
/// scope. Its sibling <see cref="Scope.ValidateGrant"/> speaks permission atoms; this one speaks the
/// scope vocabulary, so aliases are expanded before the check and a request for the alias succeeds
/// only when the caller holds every atom it widens to.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeDelegationTests
{
    [Fact]
    public void ValidateDelegation_EmptyRequestIsAllowed()
    {
        Scope.ValidateDelegation([], [Scope.GlucoseRead])
            .Should().BeNull("delegating nothing never exceeds the caller");
    }

    [Fact]
    public void ValidateDelegation_NullRequestIsAllowed()
    {
        Scope.ValidateDelegation(null, [Scope.GlucoseRead])
            .Should().BeNull("a missing request delegates nothing");
    }

    [Fact]
    public void ValidateDelegation_ReadWriteSatisfiesItsOwnReadTier()
    {
        Scope.ValidateDelegation([Scope.GlucoseRead], [Scope.GlucoseReadWrite])
            .Should().BeNull("a caller holding the write tier may hand out the read tier");
    }

    [Fact]
    public void ValidateDelegation_ReadDoesNotSatisfyItsOwnReadWriteTier()
    {
        var violation = Scope.ValidateDelegation([Scope.GlucoseReadWrite], [Scope.GlucoseRead]);

        violation.Should().NotBeNull();
        violation!.Code.Should().Be(GrantCeilingViolation.ExceedsGranter);
    }

    [Fact]
    public void ValidateDelegation_FullAccessCallerSatisfiesEveryScope()
    {
        Scope.ValidateDelegation([Scope.TreatmentsRead, Scope.TherapyRead], [Scope.FullAccess])
            .Should().BeNull("'*' spans every category");
    }

    [Fact]
    public void ValidateDelegation_AliasExpandsBeforeTheCheck()
    {
        // glucose.read alone satisfies glucose.read, but health.read widens to every core health
        // read, so the caller must be refused over the first atom it does not hold.
        var violation = Scope.ValidateDelegation([Scope.HealthRead], [Scope.GlucoseRead]);

        violation.Should().NotBeNull();
        violation!.Code.Should().Be(GrantCeilingViolation.ExceedsGranter);
    }

    [Fact]
    public void ValidateDelegation_AliasIsAllowedWhenEveryAtomIsHeld()
    {
        var everyAtom = new HashSet<string>(Scope.HealthReadExpansion, StringComparer.Ordinal);

        Scope.ValidateDelegation([Scope.HealthRead], everyAtom)
            .Should().BeNull("the caller holds every atom the alias widens to");
    }
}
