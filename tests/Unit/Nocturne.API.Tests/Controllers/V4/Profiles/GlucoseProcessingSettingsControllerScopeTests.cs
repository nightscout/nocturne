using FluentAssertions;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// The preferred glucose processing and the source defaults are one value for the whole tenant, so
/// setting either is tenant administration and needs <c>tenant.settings</c>. Reading them does not.
/// </summary>
public sealed class GlucoseProcessingSettingsControllerScopeTests
{
    private static readonly AuthContext Member = new() { IsAuthenticated = true, AuthType = AuthType.SessionCookie };

    [Theory]
    [InlineData(nameof(GlucoseProcessingSettingsController.SetPreference))]
    [InlineData(nameof(GlucoseProcessingSettingsController.SetSourceDefaults))]
    public void A_caller_without_tenant_settings_is_forbidden_before_the_handler(string action)
    {
        ActionGate.Evaluate(
                typeof(GlucoseProcessingSettingsController),
                action,
                new HashSet<string> { Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite, Scope.AlertsReadWrite },
                Member)
            .ShouldBeScopeRefusal(Scope.TenantSettings);

        ActionGate.Evaluate(
                typeof(GlucoseProcessingSettingsController), action, new HashSet<string> { Scope.TenantSettings }, Member)
            .Should().BeNull("a caller holding tenant.settings reaches the handler");
    }

    [Theory]
    [InlineData(nameof(GlucoseProcessingSettingsController.GetPreference))]
    [InlineData(nameof(GlucoseProcessingSettingsController.GetSourceDefaults))]
    public void A_member_who_reads_glucose_still_reads_the_settings(string action) =>
        ActionGate.Evaluate(
                typeof(GlucoseProcessingSettingsController), action, new HashSet<string> { Scope.GlucoseRead }, Member)
            .Should().BeNull();
}
