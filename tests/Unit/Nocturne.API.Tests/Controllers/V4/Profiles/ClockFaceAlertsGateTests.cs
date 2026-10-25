using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// A clock face hands its alerts to anyone holding its link, so only a member who may read alerts
/// can turn them on, and the face stops serving them once its owner may not.
/// </summary>
[Trait("Category", "Unit")]
public class ClockFaceAlertsGateTests
{
    private readonly Mock<IClockFaceService> _faces = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_RefusesAlertsToACallerWhoCannotReadThem(bool showAlerts, bool allowMarkSeen)
    {
        var result = await Faces(Scope.GlucoseRead, Scope.ReportsRead).Create(new CreateClockFaceRequest
        {
            Name = "Kitchen",
            Config = WithAlerts(showAlerts, allowMarkSeen),
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _faces.Verify(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<CreateClockFaceRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_AdmitsAlertsFromACallerWhoReadsThem()
    {
        _faces.Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<CreateClockFaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = Guid.NewGuid() });

        var result = await Faces(Scope.AlertsRead).Create(new CreateClockFaceRequest
        {
            Name = "Kitchen",
            Config = WithAlerts(true, true),
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Update_KeepsAlertsTheFaceAlreadyShowedForAnOwnerWhoLostThem()
    {
        var id = Guid.NewGuid();
        _faces.Setup(f => f.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = id, Config = WithAlerts(true, false) });
        _faces.Setup(f => f.UpdateAsync(id, It.IsAny<string>(), It.IsAny<UpdateClockFaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = id });

        var kept = await Faces(Scope.GlucoseRead).Update(id, new UpdateClockFaceRequest { Config = WithAlerts(true, false) });
        var widened = await Faces(Scope.GlucoseRead).Update(id, new UpdateClockFaceRequest { Config = WithAlerts(true, true) });

        kept.Result.Should().BeOfType<OkObjectResult>();
        widened.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task AFaceWhoseOwnerCannotReadAlerts_ServesNoneAndRecordsNothing()
    {
        var id = Guid.NewGuid();
        _faces.Setup(f => f.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClockFace { Id = id, UserId = _owner.ToString(), Config = WithAlerts(true, true) });
        var contexts = new Mock<INocturneDbContextFactory>(MockBehavior.Strict);
        var controller = Alerts(contexts.Object, ownerPermissions: [Scope.GlucoseRead]);

        var alerts = await controller.GetClockAlerts(id);
        var seen = await controller.MarkClockAlertSeen(id, Guid.NewGuid());

        alerts.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(Array.Empty<ClockAlertDto>());
        seen.Should().BeOfType<NotFoundResult>();
    }

    private static ClockFaceConfig WithAlerts(bool showAlerts, bool allowMarkSeen) => new()
    {
        Settings = new ClockSettings { ShowAlerts = showAlerts, AllowMarkSeen = allowMarkSeen },
    };

    private ClockFacesController Faces(params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(new AuthContext { IsAuthenticated = true, SubjectId = _owner });
        httpContext.SetGrantedScopes(new HashSet<string>(scopes));
        return new ClockFacesController(
            _faces.Object, Mock.Of<ISensorGlucoseRepository>(), Mock.Of<ILogger<ClockFacesController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };
    }

    private ClockFaceAlertsController Alerts(INocturneDbContextFactory contexts, string[] ownerPermissions)
    {
        var members = new Mock<ITenantMemberService>();
        members.Setup(m => m.GetMemberAccessAsync(_owner, _tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantMemberAccess(new HashSet<string>(ownerPermissions), false));
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.TenantId).Returns(_tenant);

        return new ClockFaceAlertsController(
            _faces.Object, contexts, new AlertReceiptService(contexts), members.Object, tenant.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
