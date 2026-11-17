using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

[Trait("Category", "Unit")]
public class TrackerLegacyFallbacksControllerTests
{
    private static readonly Guid SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid SensorTrackerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Mock<ILegacyFallbackService> _service = new();

    public TrackerLegacyFallbacksControllerTests()
    {
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site);
        var at = DateTime.UtcNow.AddHours(-10);
        _service.Setup(s => s.GetAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegacyFallbackRead([
                new LegacyFallback(
                    LegacyConsumableKind.Site,
                    new DeviceEvent { Id = Guid.NewGuid(), EventType = DeviceEventType.SiteChange, Timestamp = at },
                    spec,
                    new TrackerInstanceEntity
                    {
                        StartedAt = at,
                        Definition = ConsumableTrackerSpecs.ToDefinition(spec, string.Empty),
                    }),
            ], [new LegacyCoverage(LegacyConsumableKind.Sensor, [SensorTrackerId])]));
        _service.Setup(s => s.GetAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegacyFallbackRead(
                [], [new LegacyCoverage(LegacyConsumableKind.Sensor, [SensorTrackerId])]));
        _service.Setup(s => s.CreateTrackersAsync(
                It.IsAny<IReadOnlyCollection<LegacyConsumableKind>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegacyConversion([], [], []));
    }

    private TrackerLegacyFallbacksController Controller(params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string>(scopes);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = SubjectId,
        };
        return new TrackerLegacyFallbacksController(_service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static LegacyFallbacksDto Body(ActionResult<LegacyFallbacksDto> result) =>
        (LegacyFallbacksDto)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public async Task AReaderOfTrackersAndDevices_GetsTheFallbacks()
    {
        var body = Body(await Controller(Scope.AlertsRead, Scope.DevicesRead).GetLegacyFallbacks());

        var site = body.Fallbacks.Should().ContainSingle().Subject;
        site.Kind.Should().Be(LegacyConsumableKind.Site);
        site.Spec.Name.Should().Be("Infusion Site");
        site.Schedule.Should().HaveCount(2);
        var sensor = body.Covered.Should().ContainSingle().Subject;
        sensor.Kind.Should().Be(LegacyConsumableKind.Sensor);
        sensor.DefinitionIds.Should().Equal(SensorTrackerId);
    }

    [Fact]
    public async Task AReaderOfTrackersAlone_GetsTheCoverageAndNoFallbacks()
    {
        var body = Body(await Controller(Scope.AlertsRead).GetLegacyFallbacks());

        body.Fallbacks.Should().BeEmpty();
        body.Covered.Should().ContainSingle().Which.DefinitionIds.Should().Equal(SensorTrackerId);
        _service.Verify(s => s.GetAsync(true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACallerWithoutTrackerReadAccess_GetsNone()
    {
        var body = Body(await Controller(Scope.DevicesRead).GetLegacyFallbacks());

        body.Fallbacks.Should().BeEmpty();
        body.Covered.Should().BeEmpty();
        _service.Verify(s => s.GetAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConvertingWithoutDevicesRead_IsForbidden()
    {
        var result = await Controller(Scope.AlertsReadWrite).CreateFromLegacy(
            new CreateTrackersFromLegacyRequest { Kinds = [LegacyConsumableKind.Site] });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnUndefinedKind_IsRejectedBeforeAnyConversion()
    {
        var result = await Controller(Scope.AlertsReadWrite, Scope.DevicesRead).CreateFromLegacy(
            new CreateTrackersFromLegacyRequest { Kinds = [LegacyConsumableKind.Sensor, (LegacyConsumableKind)99] });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _service.Verify(s => s.CreateTrackersAsync(
            It.IsAny<IReadOnlyCollection<LegacyConsumableKind>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Converting_PassesTheKindsAndTheCaller()
    {
        await Controller(Scope.AlertsReadWrite, Scope.DevicesRead).CreateFromLegacy(
            new CreateTrackersFromLegacyRequest { Kinds = [LegacyConsumableKind.Sensor] });

        _service.Verify(s => s.CreateTrackersAsync(
            It.Is<IReadOnlyCollection<LegacyConsumableKind>>(k => k.Single() == LegacyConsumableKind.Sensor),
            SubjectId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ConvertingWithoutAlertsReadWrite_IsRefusedByTheGate()
    {
        var authContext = new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = SubjectId };

        var refused = ActionGate.Evaluate(
            typeof(TrackerLegacyFallbacksController),
            nameof(TrackerLegacyFallbacksController.CreateFromLegacy),
            new HashSet<string> { Scope.AlertsRead, Scope.DevicesRead },
            authContext);
        var admitted = ActionGate.Evaluate(
            typeof(TrackerLegacyFallbacksController),
            nameof(TrackerLegacyFallbacksController.CreateFromLegacy),
            new HashSet<string> { Scope.AlertsReadWrite, Scope.DevicesRead },
            authContext);

        refused.Should().BeOfType<ForbidResult>();
        admitted.Should().BeNull();
    }
}
