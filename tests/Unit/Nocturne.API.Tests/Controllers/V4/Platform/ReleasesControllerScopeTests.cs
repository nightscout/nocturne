using FluentAssertions;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The release status is for the owners who get the release notices, so it is gated on
/// <c>tenant.settings</c> ahead of the 404 a disabled release check answers.
/// </summary>
public sealed class ReleasesControllerScopeTests
{
    private static readonly AuthContext Member = new() { IsAuthenticated = true, AuthType = AuthType.SessionCookie };

    [Fact]
    public void A_caller_without_tenant_settings_is_forbidden_before_the_handler()
    {
        ActionGate.Evaluate(
                typeof(ReleasesController),
                nameof(ReleasesController.GetReleaseStatus),
                new HashSet<string> { Scope.GlucoseRead, Scope.MembersManage },
                Member)
            .ShouldBeScopeRefusal(Scope.TenantSettings);

        ActionGate.Evaluate(
                typeof(ReleasesController),
                nameof(ReleasesController.GetReleaseStatus),
                new HashSet<string> { Scope.TenantSettings },
                Member)
            .Should().BeNull("a caller holding tenant.settings reaches the handler");
    }
}
