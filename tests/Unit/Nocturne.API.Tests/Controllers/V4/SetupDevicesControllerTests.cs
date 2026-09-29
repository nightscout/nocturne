using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class SetupDevicesControllerTests
{
    private static SetupDevicesController Build(params string[] grantedScopes)
    {
        var db = TestDbContextFactory.CreateInMemoryContext($"setup_devices_{Guid.NewGuid()}");
        db.TenantId = Guid.CreateVersion7();
        db.Tenants.Add(new TenantEntity { Id = db.TenantId, Slug = "own", DisplayName = "Own" });
        db.SaveChanges();
        var factory = new TestTenantDbContextFactory(db);
        var service = new DeviceSetupService(
            db,
            new PatientDeviceRepository(factory, NullLogger<PatientDeviceRepository>.Instance),
            new PatientInsulinRepository(factory, NullLogger<PatientInsulinRepository>.Instance),
            Mock.Of<IDeviceReattributionService>(),
            new TrackerRepository(db),
            Mock.Of<ITrackerAlertRuleSyncService>(),
            Mock.Of<ITherapySettingsResolver>(t =>
                t.GetActionTimeAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>())
                    == Task.FromResult(new InsulinActionTime(InsulinActionTimeSource.Default, 3, null))));

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = Guid.CreateVersion7() };
        return new SetupDevicesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task EveryEndpoint_RefusesAMemberWhoIsNotAnOwner()
    {
        var controller = Build(Scope.DevicesReadWrite, Scope.TherapyReadWrite, Scope.AlertsReadWrite);

        (await controller.GetDeviceSetup(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.GetInsulinActionTime(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.ConfirmSetupDevice(new() { CatalogId = "dexcom-g7" }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.AddSetupInsulin(new() { FormulationId = "humalog" }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.SetTakesNoInsulin(new() { TakesNoInsulin = true }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.AddSetupTracker(new() { Kind = TrackerOfferKind.Sensor, Name = "Sensor" }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetInsulinActionTime_ReturnsTheResolvedTimeAndItsSource()
    {
        var result = await Build(Scope.FullAccess).GetInsulinActionTime(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().Be(new InsulinActionTime(InsulinActionTimeSource.Default, 3, null));
    }

    [Fact]
    public async Task ConfirmThenTracker_ReturnsTheUpdatedSetup_WithTheOwnersTracker()
    {
        var controller = Build(Scope.FullAccess);

        var confirmed = await controller.ConfirmSetupDevice(new() { CatalogId = "dexcom-g7" }, CancellationToken.None);
        var setup = confirmed.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DeviceSetup>().Subject;
        setup.Trackers.Should().ContainSingle().Which.WearDays.Should().Be(10);

        var tracked = await controller.AddSetupTracker(
            new() { Kind = TrackerOfferKind.Sensor, Name = "Dexcom G7 sensor" }, CancellationToken.None);
        tracked.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<DeviceSetup>()
            .Which.Trackers.Single().DefinitionId.Should().NotBeNull();
    }

    [Fact]
    public async Task Writes_MapABadCatalogueIdTo400_AndNoneOverAnInsulinTo409()
    {
        var controller = Build(Scope.FullAccess);

        (await controller.ConfirmSetupDevice(new() { CatalogId = "not-a-device" }, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);

        await controller.AddSetupInsulin(new() { FormulationId = "humalog" }, CancellationToken.None);
        (await controller.SetTakesNoInsulin(new() { TakesNoInsulin = true }, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }
}
