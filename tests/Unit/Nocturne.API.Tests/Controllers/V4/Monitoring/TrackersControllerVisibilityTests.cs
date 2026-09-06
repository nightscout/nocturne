using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Pins who <c>TrackersController.CanViewTracker</c> lets read a definition back.
/// The owner clause is deliberately not conditional on a visibility value, so that no visibility
/// can hide a tracker from the person who set it.
/// </summary>
[Trait("Category", "Unit")]
public class TrackersControllerVisibilityTests
{
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";
    private const string StrangerId = "22222222-2222-2222-2222-222222222222";

    private readonly Mock<ITrackerRepository> _repository = new();

    private TrackersController CreateController(string? subjectId, bool isAdmin = false)
    {
        var controller = new TrackersController(
            _repository.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<ITrackerAlertRuleSyncService>(),
            Mock.Of<ITenantDbContextFactory>(),
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<ILogger<TrackersController>>()
        );

        var httpContext = new DefaultHttpContext();
        if (subjectId != null)
        {
            httpContext.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true,
                SubjectId = Guid.Parse(subjectId),
            };
        }

        if (isAdmin)
        {
            var trie = new PermissionTrie();
            trie.Add("admin");
            httpContext.Items["PermissionTrie"] = trie;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private TrackerDefinitionEntity ArrangeDefinition(
        TrackerVisibility visibility,
        string userId = OwnerId)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Sensor",
            Visibility = visibility,
        };

        _repository
            .Setup(x => x.GetDefinitionByIdAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        _repository
            .Setup(x => x.GetDefinitionsForUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([definition]);

        return definition;
    }

    [Theory]
    [InlineData(TrackerVisibility.Public)]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(TrackerVisibility.RoleRestricted)]
    public async Task GetDefinition_OwnerReadsOwnTrackerBack_AtEveryVisibility(
        TrackerVisibility visibility)
    {
        var definition = ArrangeDefinition(visibility);
        var controller = CreateController(OwnerId);

        var result = await controller.GetDefinition(definition.Id);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(TrackerVisibility.Public)]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(TrackerVisibility.RoleRestricted)]
    public async Task GetDefinitions_OwnerSeesOwnTrackerListed_AtEveryVisibility(
        TrackerVisibility visibility)
    {
        ArrangeDefinition(visibility);
        var controller = CreateController(OwnerId);

        var result = await controller.GetDefinitions();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<TrackerDefinitionDto[]>().Which.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(TrackerVisibility.Public)]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(TrackerVisibility.RoleRestricted)]
    public async Task GetDefinition_AdminReadsAnyTracker_AtEveryVisibility(
        TrackerVisibility visibility)
    {
        var definition = ArrangeDefinition(visibility);
        var controller = CreateController(StrangerId, isAdmin: true);

        var result = await controller.GetDefinition(definition.Id);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetDefinition_NonOwnerReadsPublicTracker()
    {
        var definition = ArrangeDefinition(TrackerVisibility.Public);
        var controller = CreateController(StrangerId);

        var result = await controller.GetDefinition(definition.Id);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(TrackerVisibility.RoleRestricted)]
    public async Task GetDefinition_NonOwnerIsForbidden_WhenNotPublic(TrackerVisibility visibility)
    {
        var definition = ArrangeDefinition(visibility);
        var controller = CreateController(StrangerId);

        var result = await controller.GetDefinition(definition.Id);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(TrackerVisibility.RoleRestricted)]
    public async Task GetDefinition_AnonymousIsForbidden_WhenNotPublic(TrackerVisibility visibility)
    {
        var definition = ArrangeDefinition(visibility);
        var controller = CreateController(subjectId: null);

        var result = await controller.GetDefinition(definition.Id);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    /// <summary>
    /// An unattributed tracker (UserId left at its "" default) must not be claimed by a caller
    /// that carries no subject of its own. Owning "" is not owning anything.
    /// </summary>
    [Theory]
    [InlineData(TrackerVisibility.Private)]
    [InlineData(TrackerVisibility.RoleRestricted)]
    public async Task GetDefinition_SubjectlessCallerDoesNotOwnUnattributedTracker(
        TrackerVisibility visibility)
    {
        var definition = ArrangeDefinition(visibility, userId: string.Empty);
        var controller = CreateController(subjectId: null);

        var result = await controller.GetDefinition(definition.Id);

        result.Result.Should().BeOfType<ForbidResult>();
    }
}
