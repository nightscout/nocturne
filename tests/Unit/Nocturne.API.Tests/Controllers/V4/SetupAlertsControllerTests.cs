using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class SetupAlertsControllerTests
{
    [Theory]
    [InlineData(Scope.TenantSettings)]
    [InlineData(Scope.AlertsReadWrite)]
    public async Task EveryEndpoint_RefusesAMemberWhoIsNotAnOwner(string scope)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string> { scope };
        // Never reached: the gate refuses before the service is asked anything.
        var controller = new SetupAlertsController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        var id = Guid.CreateVersion7();

        (await controller.GetAlertSetup(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.SaveAlertSetup(new SaveAlertSetupRequest(), CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.SendSetupTestAlert(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.GetSetupTestAlert(id, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.ConfirmSetupTestAlertReceived(id, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.SetUrgentLowRecipient(id, new SetUrgentLowRecipientRequest { Alerted = true }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
    }
}
