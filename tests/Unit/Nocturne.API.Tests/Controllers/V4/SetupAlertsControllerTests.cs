using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class SetupAlertsControllerTests
{
    [Theory]
    [InlineData(nameof(SetupAlertsController.GetAlertSetup))]
    [InlineData(nameof(SetupAlertsController.SaveAlertSetup))]
    [InlineData(nameof(SetupAlertsController.SendSetupTestAlert))]
    [InlineData(nameof(SetupAlertsController.GetSetupTestAlert))]
    [InlineData(nameof(SetupAlertsController.ConfirmSetupTestAlertReceived))]
    [InlineData(nameof(SetupAlertsController.SetUrgentLowRecipient))]
    public void EveryEndpoint_RefusesAMemberWhoIsNotAnOwner(string action)
    {
        var refusal = ActionGate.Evaluate(
                typeof(SetupAlertsController),
                action,
                new HashSet<string> { Scope.AlertsReadWrite, Scope.TenantSettings },
                new AuthContext { IsAuthenticated = true, SubjectId = Guid.CreateVersion7() })
            .Should().BeOfType<ScopeForbiddenResult>().Subject;

        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refusal.Scopes.Should().Contain(Scope.FullAccess);
    }
}
