using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// A public link shares alerts only when its owner switched them on, not through a role or a broad
/// grant its Public membership held before alerts could be shared.
/// </summary>
public class SharesWithPublicTests
{
    [Fact]
    public void Alerts_NeedADirectGrant()
    {
        Scope.SharesWithPublic(Scope.AlertsRead, [Scope.GlucoseRead, Scope.AlertsRead]).Should().BeTrue();
        Scope.SharesWithPublic(Scope.AlertsRead, [Scope.GlucoseRead]).Should().BeFalse();
        Scope.SharesWithPublic(Scope.AlertsRead, [Scope.FullAccess]).Should().BeFalse();
        Scope.SharesWithPublic(Scope.AlertsRead, null).Should().BeFalse();
    }

    [Fact]
    public void EveryOtherCategory_FollowsTheResolvedGrant()
    {
        Scope.PublicShareScopes.Where(s => s != Scope.AlertsRead)
            .Should().OnlyContain(s => Scope.SharesWithPublic(s, null));
    }
}
