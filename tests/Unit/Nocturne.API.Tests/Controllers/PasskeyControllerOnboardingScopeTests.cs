using FluentAssertions;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Completing onboarding enrols the whole tenant in the setup hub, so it is tenant administration
/// and needs <c>tenant.settings</c>.
/// </summary>
public sealed class PasskeyControllerOnboardingScopeTests
{
    private static readonly AuthContext Member = new() { IsAuthenticated = true, AuthType = AuthType.SessionCookie };

    [Fact]
    public void A_caller_without_tenant_settings_is_forbidden_before_the_handler()
    {
        ActionGate.Evaluate(
                typeof(PasskeyController),
                nameof(PasskeyController.CompleteOnboarding),
                new HashSet<string> { Scope.GlucoseRead, Scope.TreatmentsReadWrite, Scope.AlertsReadWrite },
                Member)
            .ShouldBeScopeRefusal(Scope.TenantSettings);

        ActionGate.Evaluate(
                typeof(PasskeyController),
                nameof(PasskeyController.CompleteOnboarding),
                new HashSet<string> { Scope.FullAccess },
                Member)
            .Should().BeNull("the owner finishing first-run setup holds full access");
    }
}
