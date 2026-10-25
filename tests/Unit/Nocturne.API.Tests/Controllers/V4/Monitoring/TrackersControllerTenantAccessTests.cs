using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Pins that trackers are the tenant's: every member reads every tracker whoever created it,
/// provided they hold <c>alerts.read</c>; any member holding the write
/// scope changes any of them; and a viewer without an account (a guest link, a public share, no
/// credential) reads none even when it holds <c>alerts.read</c>.
/// </summary>
[Trait("Category", "Unit")]
public class TrackersControllerTenantAccessTests
{
    private const string CreatorId = "11111111-1111-1111-1111-111111111111";
    private const string MemberId = "22222222-2222-2222-2222-222222222222";

    public enum Viewer { Guest, Share, NoCredential }

    private readonly Mock<ITrackerRepository> _repository = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<IAlertRepository> _alerts = new();

    public TrackersControllerTenantAccessTests()
    {
        _alerts
            .Setup(a => a.GetUnacknowledgedExcursionIdsAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));
    }

    private TrackersController Controller(HttpContext httpContext)
    {
        var controller = new TrackersController(
            _repository.Object,
            _broadcast.Object,
            Mock.Of<ITrackerAlertRuleSyncService>(),
            _alerts.Object,
            Mock.Of<ITenantAccessor>(),
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<ILogger<TrackersController>>()
        );
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private TrackersController AsMember(string subjectId = MemberId, string scope = Scope.AlertsRead)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { scope };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.Parse(subjectId),
        };
        return Controller(httpContext);
    }

    /// <summary>
    /// Every viewer holds <c>alerts.read</c>, so what refuses it is the account check, not the scope.
    /// </summary>
    private TrackersController As(Viewer viewer)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { Scope.AlertsRead };
        switch (viewer)
        {
            case Viewer.Guest:
                httpContext.Items["AuthContext"] = new AuthContext
                {
                    IsAuthenticated = true,
                    AuthType = AuthType.Guest,
                    ActingAsSubjectId = Guid.Parse(CreatorId),
                };
                break;
            case Viewer.Share:
                httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = false };
                httpContext.SetShareAccess();
                break;
        }

        return Controller(httpContext);
    }

    private TrackerDefinitionEntity Definition(string createdBy = CreatorId)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            CreatedBy = createdBy,
            Name = "Sensor",
            LifespanHours = 240,
        };
        _repository
            .Setup(r => r.GetDefinitionByIdAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        return definition;
    }

    private TrackerInstanceEntity Instance(TrackerDefinitionEntity definition)
    {
        var instance = new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            CreatedBy = CreatorId,
            DefinitionId = definition.Id,
            Definition = definition,
            StartedAt = DateTime.UtcNow.AddDays(-1),
        };
        _repository
            .Setup(r => r.GetInstanceByIdAsync(instance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        return instance;
    }

    private TrackerPresetEntity Preset(TrackerDefinitionEntity definition)
    {
        var preset = new TrackerPresetEntity
        {
            Id = Guid.NewGuid(),
            CreatedBy = CreatorId,
            Name = "Preset",
            DefinitionId = definition.Id,
            Definition = definition,
        };
        _repository
            .Setup(r => r.GetPresetByIdAsync(preset.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preset);
        return preset;
    }

    // ── Reads ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDefinitions_MemberSeesEveryTracker_WhoeverCreatedIt()
    {
        var definitions = new List<TrackerDefinitionEntity>
        {
            Definition(createdBy: CreatorId),
            Definition(createdBy: MemberId),
            Definition(createdBy: "33333333-3333-3333-3333-333333333333"),
        };
        _repository.Setup(r => r.GetAllDefinitionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(definitions);

        var result = await AsMember().GetDefinitions();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerDefinitionDto[]>()
            .Which.Select(d => d.Id).Should().BeEquivalentTo(definitions.Select(d => d.Id));
    }

    [Fact]
    public async Task GetDefinitions_ByCategory_ReadsTheTenantsDefinitionsInIt()
    {
        var definition = Definition();
        _repository
            .Setup(r => r.GetDefinitionsByCategoryAsync(TrackerCategory.Sensor, It.IsAny<CancellationToken>()))
            .ReturnsAsync([definition]);

        var result = await AsMember().GetDefinitions(TrackerCategory.Sensor);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerDefinitionDto[]>().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDefinition_MemberReadsATrackerTheyDidNotCreate()
    {
        var definition = Definition();

        var result = await AsMember().GetDefinition(definition.Id);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(Viewer.Guest)]
    [InlineData(Viewer.Share)]
    [InlineData(Viewer.NoCredential)]
    public Task ViewerWithoutAnAccount_ReadsNoTracker(Viewer viewer) => ReadsNothing(As(viewer));

    [Theory]
    [InlineData(Scope.GlucoseRead)]
    [InlineData(Scope.ReportsRead)]
    public Task MemberWithoutAlertsRead_ReadsNoTracker(string scope) =>
        ReadsNothing(AsMember(scope: scope));

    [Theory]
    [InlineData(Scope.AlertsReadWrite)]
    [InlineData(Scope.FullAccess)]
    public async Task MemberWithAScopeSatisfyingAlertsRead_ReadsTrackers(string scope)
    {
        var definition = Definition();

        var result = await AsMember(scope: scope).GetDefinition(definition.Id);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private async Task ReadsNothing(TrackersController controller)
    {
        var definition = Definition();

        (await controller.GetDefinitions()).Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerDefinitionDto[]>().Which.Should().BeEmpty();
        (await controller.GetDefinition(definition.Id)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.GetActiveInstances()).Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerInstanceDto[]>().Which.Should().BeEmpty();
        (await controller.GetInstanceHistory()).Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerInstanceDto[]>().Which.Should().BeEmpty();
        (await controller.GetUpcomingInstances()).Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerInstanceDto[]>().Which.Should().BeEmpty();
        (await controller.GetPresets()).Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerPresetDto[]>().Which.Should().BeEmpty();

        _repository.VerifyNoOtherCalls();
    }

    // ── Writes by a member who did not create the tracker ───────────────────────────────────

    [Fact]
    public async Task UpdateDefinition_ByAMemberWhoDidNotCreateIt_Updates()
    {
        var definition = Definition();
        _repository
            .Setup(r => r.UpdateDefinitionAsync(definition.Id, It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TrackerDefinitionEntity e, CancellationToken _) => e);

        var result = await AsMember().UpdateDefinition(
            definition.Id, new UpdateTrackerDefinitionRequest { Name = "Renamed" });

        result.Result.Should().BeOfType<OkObjectResult>();
        _repository.Verify(r => r.UpdateDefinitionAsync(
            definition.Id, It.Is<TrackerDefinitionEntity>(e => e.Name == "Renamed" && e.CreatedBy == CreatorId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteDefinition_ByAMemberWhoDidNotCreateIt_Deletes()
    {
        var definition = Definition();

        var result = await AsMember().DeleteDefinition(definition.Id);

        result.Should().BeOfType<NoContentResult>();
        _repository.Verify(r => r.DeleteDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AckInstance_ByAMemberWhoDidNotStartIt_Acknowledges()
    {
        var instance = Instance(Definition());

        var result = await AsMember().AckInstance(instance.Id, new AckTrackerRequest { SnoozeMins = 30 });

        result.Should().BeOfType<NoContentResult>();
        _repository.Verify(r => r.AckInstanceAsync(instance.Id, 30, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AckInstance_BroadcastsTheAck()
    {
        var instance = Instance(Definition());

        await AsMember().AckInstance(instance.Id, new AckTrackerRequest { SnoozeMins = 30 });

        _broadcast.Verify(b => b.BroadcastTrackerUpdateAsync(
            TrackerRealtimeAction.Ack, It.Is<TrackerInstanceDto>(d => d.Id == instance.Id)), Times.Once);
    }

    [Fact]
    public async Task CompleteInstance_ByAMemberWhoDidNotStartIt_Completes()
    {
        var instance = Instance(Definition());
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                instance.Id, CompletionReason.Completed, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        var result = await AsMember().CompleteInstance(
            instance.Id, new CompleteTrackerInstanceRequest { Reason = CompletionReason.Completed });

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Presets_OfAnotherMember_CanBeCreatedAppliedAndDeleted()
    {
        var definition = Definition();
        var preset = Preset(definition);
        _repository
            .Setup(r => r.CreatePresetAsync(It.IsAny<TrackerPresetEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerPresetEntity p, CancellationToken _) => p);
        _repository
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repository
            .Setup(r => r.StartInstanceAsync(
                definition.Id, MemberId, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackerInstanceEntity { Id = Guid.NewGuid(), DefinitionId = definition.Id, Definition = definition });

        var controller = AsMember();

        (await controller.CreatePreset(new CreateTrackerPresetRequest { Name = "Mine", DefinitionId = definition.Id }))
            .Result.Should().BeOfType<CreatedResult>();
        (await controller.ApplyPreset(preset.Id)).Result.Should().BeOfType<OkObjectResult>();
        (await controller.DeletePreset(preset.Id)).Should().BeOfType<NoContentResult>();
        _repository.Verify(r => r.DeletePresetAsync(preset.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(nameof(TrackersController.CreateDefinition))]
    [InlineData(nameof(TrackersController.UpdateDefinition))]
    [InlineData(nameof(TrackersController.DeleteDefinition))]
    [InlineData(nameof(TrackersController.StartInstance))]
    [InlineData(nameof(TrackersController.CompleteInstance))]
    [InlineData(nameof(TrackersController.AckInstance))]
    [InlineData(nameof(TrackersController.DeleteInstance))]
    [InlineData(nameof(TrackersController.CreatePreset))]
    [InlineData(nameof(TrackersController.ApplyPreset))]
    [InlineData(nameof(TrackersController.DeletePreset))]
    public void Write_ByAMemberWithoutAlertsReadWrite_IsForbidden(string action)
    {
        var member = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.Parse(MemberId),
        };

        ActionGate.Evaluate(typeof(TrackersController), action, new HashSet<string> { Scope.AlertsRead }, member)
            .Should().BeOfType<ForbidResult>();
        ActionGate.Evaluate(typeof(TrackersController), action, new HashSet<string> { Scope.AlertsReadWrite }, member)
            .Should().BeNull();
    }

    // ── Definition broadcasts ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDefinition_BroadcastsTheDefinitionChange()
    {
        var createdId = Guid.NewGuid();
        _repository
            .Setup(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerDefinitionEntity e, CancellationToken _) =>
            {
                e.Id = createdId;
                return e;
            });

        await AsMember().CreateDefinition(new CreateTrackerDefinitionRequest { Name = "Sensor" });

        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(createdId), Times.Once);
    }

    [Fact]
    public async Task UpdateDefinition_BroadcastsTheDefinitionChange()
    {
        var definition = Definition();
        _repository
            .Setup(r => r.UpdateDefinitionAsync(definition.Id, It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TrackerDefinitionEntity e, CancellationToken _) => e);

        await AsMember().UpdateDefinition(definition.Id, new UpdateTrackerDefinitionRequest { Name = "Renamed" });

        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(definition.Id), Times.Once);
    }

    [Fact]
    public async Task DeleteDefinition_BroadcastsTheDefinitionChange()
    {
        var definition = Definition();

        await AsMember().DeleteDefinition(definition.Id);

        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(definition.Id), Times.Once);
    }

    [Fact]
    public async Task DeleteDefinition_WhenMissing_BroadcastsNothing()
    {
        await AsMember().DeleteDefinition(Guid.NewGuid());

        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(It.IsAny<Guid>()), Times.Never);
    }
}
