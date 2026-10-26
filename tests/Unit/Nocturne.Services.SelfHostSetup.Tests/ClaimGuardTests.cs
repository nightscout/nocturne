using FluentAssertions;
using Nocturne.Services.SelfHostSetup;
using Xunit;

namespace Nocturne.Services.SelfHostSetup.Tests;

public class ClaimGuardTests
{
    [Fact]
    public void TheInstanceCodeIsAccepted()
    {
        new ClaimGuard("i-0123456789abcdef0").Check("i-0123456789abcdef0").Should().Be(ClaimResult.Accepted);
    }

    [Fact]
    public void SurroundingWhitespaceFromAPasteIsIgnored()
    {
        new ClaimGuard("i-0123456789abcdef0").Check("  i-0123456789abcdef0\n").Should().Be(ClaimResult.Accepted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("i-0123456789abcdef1")]
    public void AnythingElseIsRejected(string? attempt)
    {
        new ClaimGuard("i-0123456789abcdef0").Check(attempt).Should().Be(ClaimResult.Rejected);
    }

    [Fact]
    public void FiveWrongCodesLockSetupEvenAgainstTheRightOne()
    {
        var guard = new ClaimGuard("i-0123456789abcdef0");
        for (var i = 0; i < 5; i++)
            guard.Check("wrong").Should().Be(ClaimResult.Rejected);

        guard.Check("i-0123456789abcdef0").Should().Be(ClaimResult.LockedOut);
    }

    [Fact]
    public void ARightCodeDoesNotResetTheFailureCount()
    {
        var guard = new ClaimGuard("code");
        for (var i = 0; i < 4; i++)
            guard.Check("wrong");
        guard.Check("code");
        guard.Check("wrong");

        guard.Check("code").Should().Be(ClaimResult.LockedOut);
    }

    [Fact]
    public void AnEmptyInstanceCodeIsAProgrammingError()
    {
        var act = () => new ClaimGuard(" ");

        act.Should().Throw<ArgumentException>();
    }
}
