using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins <see cref="Scope.ForDisplay"/>, the one form a grant's scopes are listed in.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeDisplayFormTests
{
    [Fact]
    public void A_guest_grant_lists_only_what_a_guest_can_hold()
    {
        var listed = Scope.ForDisplay([Scope.HealthRead], OAuthGrantTypes.Guest);

        listed.Should().Contain([Scope.GlucoseRead, Scope.TreatmentsRead]);
        listed.Should().NotContain([Scope.FoodRead, Scope.HealthRead]);
        listed.Should().OnlyContain(s => Scope.AllowedGuestScopes.Contains(s));
        listed.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(OAuthGrantTypes.Direct)]
    [InlineData(OAuthGrantTypes.App)]
    [InlineData(OAuthGrantTypes.Follower)]
    public void Any_other_grant_lists_its_storage_form_without_atoms_a_client_cannot_hold(string grantType)
    {
        Scope.ForDisplay(
                [Scope.MembersManage, Scope.TreatmentsRead, Scope.GlucoseRead, Scope.AuditRead], grantType)
            .Should().Equal(Scope.GlucoseRead, Scope.TreatmentsRead);

        Scope.ForDisplay([Scope.RolesManage, Scope.FullAccess], grantType)
            .Should().Equal(Scope.FullAccess);
    }
}
